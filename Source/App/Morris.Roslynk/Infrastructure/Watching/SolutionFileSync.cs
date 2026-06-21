using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Infrastructure.Watching;

/// <summary>
/// The freshness logic for one loaded solution: given a path that changed on disk, decide how to react.
/// Anything under <c>obj</c>/<c>bin</c> is ignored as build noise. A <c>.cs</c> edit to a known document is
/// folded into the snapshot incrementally via <see cref="Solution.WithDocumentText"/>; every other change
/// outside <c>obj</c>/<c>bin</c> (a project / props / sln file, an additional document, or any other watched
/// file) marks the instance dirty so the registry reloads it on next use. This is a freshness optimization,
/// not a correctness mechanism; the apply pipeline's stale-write guard is what actually protects the user,
/// so a missed event only costs a stale read until the next one.
/// </summary>
public sealed class SolutionFileSync
{
	private static readonly HashSet<string> BuildFileExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".csproj",
		".vbproj",
		".fsproj",
		".props",
		".targets",
		".sln",
		".slnx",
	};

	private static readonly string[] AncestorBuildFileNames =
	[
		"Directory.Build.props",
		"Directory.Build.targets",
		"Directory.Packages.props",
		".editorconfig",
	];

	private readonly RoslynInstance Instance;

	/// <summary>The hash of each build file as it was on disk at load, so we can tell a real edit from a touch.</summary>
	private readonly ConcurrentDictionary<string, string> BuildFileBaseline;

	/// <summary>Whether each project file uses default compile globs, so a new .cs can be folded in vs reloaded.</summary>
	private readonly ConcurrentDictionary<string, bool> UsesDefaultCompileItemsCache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The paths that are additional documents (the "C# analyzer additional file" build action) at load,
	/// so a <c>.cs</c> among them is routed to a reload instead of being folded in as a compiled source file.</summary>
	private readonly HashSet<string> AdditionalFilePaths;

	public SolutionFileSync(RoslynInstance instance)
	{
		Instance = instance ?? throw new ArgumentNullException(nameof(instance));
		BuildFileBaseline = CaptureBuildFileHashes(instance.CurrentSolution);
		AdditionalFilePaths = CaptureAdditionalFilePaths(instance.CurrentSolution);
	}

	/// <summary>
	/// The directories to watch: every project directory recursively (for new globbed files), plus the
	/// distinct out-of-tree directories that host a linked document or an ancestor build file, watched
	/// shallowly. Recomputed by the caller after each reload, since a project edit can change the set.
	/// </summary>
	public IReadOnlyList<WatchTarget> WatchTargets()
	{
		Solution solution = Instance.CurrentSolution;

		var projectDirs = new List<string>();
		foreach (Project project in solution.Projects)
		{
			if (project.FilePath is null)
				continue;
			string dir = System.IO.Path.GetDirectoryName(project.FilePath)!;
			if (!projectDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
				projectDirs.Add(dir);
		}

		var targets = new List<WatchTarget>();
		foreach (string dir in projectDirs)
			targets.Add(new WatchTarget(dir, recursive: true));

		var extraDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in DocumentAndBuildFilePaths(solution))
		{
			if (IsIgnored(path))
				continue;
			string dir = System.IO.Path.GetDirectoryName(path)!;
			if (!projectDirs.Any(projectDir => IsUnder(dir, projectDir)))
				extraDirs.Add(dir);
		}

		foreach (string dir in extraDirs)
			targets.Add(new WatchTarget(dir, recursive: false));

		return targets;
	}

	/// <summary>Reacts to a single path that changed on disk. Safe to call for any path; noise is ignored.</summary>
	public async Task OnFileChangedAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path) || IsIgnored(path))
			return;

		if (IsBuildFile(path))
		{
			OnBuildFileChanged(path);
			return;
		}

		if (IsSourceFile(path))
		{
			await OnSourceFileChangedAsync(path, cancellationToken);
			return;
		}

		// Any other file outside obj/bin can still affect the build: an additional document
		// (.razor/.cshtml/.resx), a source-generator input, or content the project globs. It cannot be
		// folded incrementally, so mark the instance dirty and let the next read reload. MarkDirty is lazy
		// and idempotent, so even a burst of unrelated changes costs at most one reload.
		Instance.MarkDirty();
	}

	private async Task OnSourceFileChangedAsync(string path, CancellationToken cancellationToken)
	{
		// A .cs file can carry the "C# analyzer additional file" build action, making it an AdditionalDocument
		// rather than a Document. It must not be folded in as compiled source; a reload re-runs the source
		// generators that consume it via AnalyzerOptions.AdditionalFiles. (Non-.cs additional files already
		// reach MarkDirty through the catch-all in OnFileChangedAsync, and a build-action change edits the
		// .csproj, which is a build file and likewise reloads.)
		if (AdditionalFilePaths.Contains(path))
		{
			Instance.MarkDirty();
			return;
		}

		Solution current = Instance.CurrentSolution;
		bool known = !current.GetDocumentIdsWithFilePath(path).IsEmpty;

		if (!File.Exists(path))
		{
			// Deleted: drop a known document incrementally; an unknown path needs nothing.
			if (known)
				await FoldRemoveAsync(path, cancellationToken);
			return;
		}

		if (!known)
		{
			// A new .cs file. Fold it into every project whose directory owns it and uses default compile
			// globs; otherwise its membership is MSBuild's call, so mark dirty and reload on next use.
			if (TryFindDefaultGlobProjects(current, path, out IReadOnlyList<ProjectId> projects))
				await FoldAddAsync(path, projects, cancellationToken);
			else
				Instance.MarkDirty();
			return;
		}

		await FoldTextAsync(current, path, cancellationToken);
	}

	private async Task FoldTextAsync(Solution snapshot, string path, CancellationToken cancellationToken)
	{
		string diskText;
		try
		{
			diskText = await File.ReadAllTextAsync(path, cancellationToken);
		}
		catch (IOException)
		{
			return; // File momentarily locked by the editor; a later event or the dirty path will catch up.
		}

		// Cheap pre-check against the current snapshot so our own writes / identical saves do not enqueue a
		// no-op; the transform re-applies authoritatively against the latest snapshot under the write lock.
		ImmutableArray<DocumentId> ids = snapshot.GetDocumentIdsWithFilePath(path);
		Document? document = ids.IsEmpty ? null : snapshot.GetDocument(ids[0]);
		if (document is null)
			return;

		string loaded = (await document.GetTextAsync(cancellationToken)).ToString();
		if (string.Equals(loaded, diskText, StringComparison.Ordinal))
			return; // Our own write, an editor touch, or a save with identical bytes.

		await Instance.EnqueueWriteAsync((current, token) =>
		{
			SourceText newText = SourceText.From(diskText);
			Solution updated = current;
			foreach (DocumentId id in current.GetDocumentIdsWithFilePath(path))
			{
				if (updated.GetDocument(id) is not null)
					updated = updated.WithDocumentText(id, newText);
			}

			return Task.FromResult(new WriteResult(updated, []));
		}, cancellationToken);
	}

	private async Task FoldRemoveAsync(string path, CancellationToken cancellationToken)
	{
		await Instance.EnqueueWriteAsync((current, token) =>
		{
			Solution updated = current;
			foreach (DocumentId id in current.GetDocumentIdsWithFilePath(path))
				updated = updated.RemoveDocument(id);

			return Task.FromResult(new WriteResult(updated, []));
		}, cancellationToken);
	}

	private async Task FoldAddAsync(string path, IReadOnlyList<ProjectId> projects, CancellationToken cancellationToken)
	{
		string diskText;
		try
		{
			diskText = await File.ReadAllTextAsync(path, cancellationToken);
		}
		catch (IOException)
		{
			return;
		}

		await Instance.EnqueueWriteAsync((current, token) =>
		{
			string name = System.IO.Path.GetFileName(path);
			Solution updated = current;
			foreach (ProjectId projectId in projects)
			{
				if (updated.GetProject(projectId) is null)
					continue;
				if (updated.GetDocumentIdsWithFilePath(path).Any(id => id.ProjectId == projectId))
					continue;

				DocumentId documentId = DocumentId.CreateNewId(projectId);
				updated = updated.AddDocument(documentId, name, SourceText.From(diskText), filePath: path);
			}

			return Task.FromResult(new WriteResult(updated, []));
		}, cancellationToken);
	}

	private bool TryFindDefaultGlobProjects(Solution solution, string path, out IReadOnlyList<ProjectId> projects)
	{
		var matches = new List<ProjectId>();
		string? fileDirectory = System.IO.Path.GetDirectoryName(path);
		if (fileDirectory is not null)
		{
			foreach (Project project in solution.Projects)
			{
				if (project.FilePath is null)
					continue;

				string projectDirectory = System.IO.Path.GetDirectoryName(project.FilePath)!;
				if (IsUnder(fileDirectory, projectDirectory) && UsesDefaultCompileItems(project.FilePath))
					matches.Add(project.Id);
			}
		}

		projects = matches;
		return matches.Count > 0;
	}

	private bool UsesDefaultCompileItems(string projectFilePath) =>
		UsesDefaultCompileItemsCache.GetOrAdd(projectFilePath, file =>
		{
			string text;
			try
			{
				text = File.ReadAllText(file);
			}
			catch (IOException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}

			// Default SDK globs are signalled by the absence of explicit compile items / opt-out.
			return !text.Contains("<EnableDefaultCompileItems>false", StringComparison.OrdinalIgnoreCase)
				&& !text.Contains("<Compile Include", StringComparison.OrdinalIgnoreCase)
				&& !text.Contains("<Compile Remove", StringComparison.OrdinalIgnoreCase);
		});

	private void OnBuildFileChanged(string path)
	{
		if (!File.Exists(path))
		{
			if (BuildFileBaseline.TryRemove(path, out _))
				Instance.MarkDirty(); // A tracked build file was deleted.
			return;
		}

		string? hash = TryHashFile(path);
		if (hash is null)
			return; // Could not read it; let a later event decide.

		if (BuildFileBaseline.TryGetValue(path, out string? baseline) && string.Equals(hash, baseline, StringComparison.Ordinal))
			return; // Unchanged content; a touch or a duplicate event.

		BuildFileBaseline[path] = hash;
		Instance.MarkDirty();
	}

	private IEnumerable<string> DocumentAndBuildFilePaths(Solution solution)
	{
		foreach (Project project in solution.Projects)
		{
			foreach (Document document in project.Documents)
			{
				if (document.FilePath is not null)
					yield return document.FilePath;
			}

			foreach (TextDocument document in project.AdditionalDocuments)
			{
				if (document.FilePath is not null)
					yield return document.FilePath;
			}
		}

		foreach (string path in BuildFileBaseline.Keys)
			yield return path;
	}

	private static ConcurrentDictionary<string, string> CaptureBuildFileHashes(Solution solution)
	{
		var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		void Track(string? candidate)
		{
			if (candidate is null || IsIgnored(candidate))
				return;
			string? hash = TryHashFile(candidate);
			if (hash is not null)
				map[candidate] = hash;
		}

		Track(solution.FilePath);
		string? solutionDir = solution.FilePath is null ? null : System.IO.Path.GetDirectoryName(solution.FilePath);

		foreach (Project project in solution.Projects)
		{
			Track(project.FilePath);
			if (project.FilePath is not null)
				TrackAncestorBuildFiles(System.IO.Path.GetDirectoryName(project.FilePath)!, solutionDir, Track);
		}

		return map;
	}

	private static HashSet<string> CaptureAdditionalFilePaths(Solution solution)
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Project project in solution.Projects)
		{
			foreach (TextDocument document in project.AdditionalDocuments)
			{
				if (document.FilePath is not null)
					paths.Add(document.FilePath);
			}
		}

		return paths;
	}

	private static void TrackAncestorBuildFiles(string startDir, string? stopDir, Action<string?> track)
	{
		DirectoryInfo? directory = new(startDir);
		while (directory is not null)
		{
			foreach (string name in AncestorBuildFileNames)
				track(System.IO.Path.Combine(directory.FullName, name));

			if (stopDir is not null && string.Equals(directory.FullName, stopDir, StringComparison.OrdinalIgnoreCase))
				break;

			directory = directory.Parent;
		}
	}

	private static bool IsBuildFile(string path) =>
		BuildFileExtensions.Contains(System.IO.Path.GetExtension(path))
		|| string.Equals(System.IO.Path.GetFileName(path), ".editorconfig", StringComparison.OrdinalIgnoreCase);

	private static bool IsSourceFile(string path) =>
		string.Equals(System.IO.Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

	private static bool IsIgnored(string path)
	{
		foreach (string segment in path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
		{
			if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	private static bool IsUnder(string child, string ancestor) =>
		string.Equals(child, ancestor, StringComparison.OrdinalIgnoreCase)
		|| child.StartsWith(ancestor + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private static string? TryHashFile(string path)
	{
		try
		{
			return File.Exists(path) ? FileHash.Of(File.ReadAllBytes(path)) : null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}
}
