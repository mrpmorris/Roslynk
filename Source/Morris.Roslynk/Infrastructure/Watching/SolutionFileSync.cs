using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Infrastructure.Watching;

/// <summary>
/// The freshness logic for one loaded solution: given a path that changed on disk, decide whether it is
/// noise (our own write, an editor touch, anything under <c>obj</c>/<c>bin</c>) and otherwise route it.
/// A <c>.cs</c> edit is folded into the snapshot incrementally via <see cref="Solution.WithDocumentText"/>;
/// a project / props / sln edit marks the instance dirty so the registry reloads it on next use. This is a
/// freshness optimization, not a correctness mechanism; the apply pipeline's stale-write guard is what
/// actually protects the user, so a missed event only costs a stale read until the next one.
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

	public SolutionFileSync(RoslynInstance instance)
	{
		Instance = instance ?? throw new ArgumentNullException(nameof(instance));
		BuildFileBaseline = CaptureBuildFileHashes(instance.CurrentSolution);
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
			await OnSourceFileChangedAsync(path, cancellationToken);
	}

	private async Task OnSourceFileChangedAsync(string path, CancellationToken cancellationToken)
	{
		if (Instance.CurrentSolution.GetDocumentIdsWithFilePath(path).IsEmpty)
		{
			// A .cs file that is not (yet) a known document; a new file under a glob, for example. That
			// changes the compilation structurally, so let the next read reload rather than guess.
			Instance.MarkDirty();
			return;
		}

		if (!File.Exists(path))
		{
			Instance.MarkDirty();
			return;
		}

		string diskText;
		try
		{
			diskText = await File.ReadAllTextAsync(path, cancellationToken);
		}
		catch (IOException)
		{
			return; // File momentarily locked by the editor; a later event or the dirty path will catch up.
		}

		await Instance.WriteLock.WaitAsync(cancellationToken);
		try
		{
			Solution current = Instance.CurrentSolution;
			ImmutableArray<DocumentId> ids = current.GetDocumentIdsWithFilePath(path);
			if (ids.IsEmpty)
				return;

			Document? document = current.GetDocument(ids[0]);
			if (document is null)
				return; // The path maps only to additional/analyzer documents, which we do not fold in yet.

			string loaded = (await document.GetTextAsync(cancellationToken)).ToString();
			if (string.Equals(loaded, diskText, StringComparison.Ordinal))
				return; // Our own write, an editor touch, or a save with identical bytes.

			SourceText newText = SourceText.From(diskText);
			Solution updated = current;
			foreach (DocumentId id in ids)
			{
				if (updated.GetDocument(id) is not null)
					updated = updated.WithDocumentText(id, newText);
			}

			Instance.AdvanceTo(updated);
		}
		finally
		{
			Instance.WriteLock.Release();
		}
	}

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
