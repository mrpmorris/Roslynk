using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Infrastructure.Watching;

/// <summary>
	/// The freshness logic for one loaded solution: given a path that changed on disk, decide how to react.
	/// Anything under <c>obj</c>/<c>bin</c> is ignored as build noise, as are the staging siblings
	/// <see cref="AtomicFileWriter"/> creates during the server's own writes (they land inside watched source
	/// directories, so without this every applied edit would dirty the instance and force a full reload; the
	/// rewritten target file itself is harmless because its fold compares content and finds the snapshot
	/// already matches). A <c>.cs</c> edit to a known document is folded into the snapshot incrementally via
	/// <see cref="Solution.WithDocumentText"/>; every other change outside <c>obj</c>/<c>bin</c> (a project /
	/// props / sln file, an additional document, or any other watched file) marks the instance dirty so the
	/// registry reloads it on next use. This is a freshness optimization, not a correctness mechanism; the
	/// apply pipeline's stale-write guard is what actually protects the user, so a missed event only costs a
	/// stale read until the next one.
	/// <para>
	/// A known additional document whose content did not change (an editor save with identical bytes, or the
	/// server's own write) needs nothing. A .razor/.cshtml edit in a project whose Razor output Roslynk generates
	/// is folded in place: the new text is applied and the generator rerun incrementally, so it costs
	/// milliseconds instead of a reload. A C# fold that changes declarations reruns the generator too, because
	/// generated Razor code binds to them (a code-behind parameter rename changes every page using the component);
	/// an edit confined to method bodies cannot change generated code. Files inside a project's
	/// dot-folders (.git, .vs, .idea) or node_modules are noise the SDK's own item globs exclude; only build
	/// files there still count.
	/// </para>
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
		private readonly DiagnosticsService DiagnosticsService;

		/// <summary>The hash of each build file as it was on disk at load, so we can tell a real edit from a touch.</summary>
		private readonly ConcurrentDictionary<string, string> BuildFileBaseline;

		/// <summary>Whether each project file uses default compile globs, so a new .cs can be folded in vs reloaded.</summary>
		private readonly ConcurrentDictionary<string, bool> UsesDefaultCompileItemsCache = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The paths that are additional documents (the "C# analyzer additional file" build action) at load,
		/// so a <c>.cs</c> among them is routed to a reload instead of being folded in as a compiled source file.</summary>
		private readonly HashSet<string> AdditionalFilePaths;

		/// <summary>The project directories at load, under which dot-folders and node_modules are ignored.</summary>
		private readonly IReadOnlyList<string> ProjectDirectories;

public SolutionFileSync(RoslynInstance instance, DiagnosticsService? diagnosticsService = null)
		{
			Instance = instance ?? throw new ArgumentNullException(nameof(instance));
			DiagnosticsService = diagnosticsService ?? new DiagnosticsService();
			BuildFileBaseline = CaptureBuildFileHashes(instance.CurrentSolution);
			AdditionalFilePaths = CaptureAdditionalFilePaths(instance.CurrentSolution);
			ProjectDirectories = CaptureProjectDirectories(instance.CurrentSolution);
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

		if (IsInIgnoredProjectFolder(path))
			return;

		if (IsSourceFile(path))
		{
			await OnSourceFileChangedAsync(path, cancellationToken);
			return;
		}

		if (await TryHandleAdditionalDocumentAsync(path, cancellationToken))
			return;

		// Any other file outside obj/bin can still affect the build: a new additional document
		// (.razor/.cshtml), a .resx, a source-generator input, or content the project globs. It cannot be
		// folded incrementally, so mark the instance dirty and let the next read reload. MarkDirty is lazy
		// and idempotent, so even a burst of unrelated changes costs at most one reload.
		Instance.MarkDirty();
	}

	/// <summary>
	/// Handles a change to a known additional document; returns false for a path that is not one (or was
	/// deleted), which the caller treats as a membership change. Unchanged content needs nothing. A Razor source
	/// whose projects all have their Razor generated by Roslynk is folded in place; any other change reloads.
	/// </summary>
	private async Task<bool> TryHandleAdditionalDocumentAsync(string path, CancellationToken cancellationToken)
	{
		Solution current = Instance.CurrentSolution;
		ImmutableArray<DocumentId> ids = [.. current.GetDocumentIdsWithFilePath(path).Where(id => current.GetAdditionalDocument(id) is not null)];
		if (ids.IsEmpty || !File.Exists(path))
			return false;

		string diskText;
		try
		{
			diskText = await File.ReadAllTextAsync(path, cancellationToken);
		}
		catch (IOException)
		{
			return true; // File momentarily locked by the editor; a later event will catch up.
		}

		SourceText loaded = await current.GetAdditionalDocument(ids[0])!.GetTextAsync(cancellationToken);
		bool unchanged = string.Equals(loaded.ToString(), diskText, StringComparison.Ordinal);

		RazorGenerationState? razor = Instance.Workspace?.Razor;
		if (!RazorDocumentGenerator.IsRazorSourcePath(path) || razor is null || !ids.All(id => razor.Covers(id.ProjectId)))
		{
			if (!unchanged)
				Instance.MarkDirty();
			return true;
		}

		// Folded even when the text matches: after the server's own write (a rename reaching a .razor file) the
		// generated documents hold the folded approximation, and regenerating replaces it with the generator's
		// real output. Nothing changes otherwise, and an unchanged result costs no diagnostics rebuild.
		await FoldRazorAsync(path, diskText, cancellationToken);
		return true;
	}

	private async Task FoldRazorAsync(string path, string diskText, CancellationToken cancellationToken)
	{
		await Instance.EnqueueWriteWithAutoDiagnosticsAsync(
			async (current, token) =>
			{
				Solution updated = current;
				var projects = new HashSet<ProjectId>();
				foreach (DocumentId id in current.GetDocumentIdsWithFilePath(path))
				{
					if (updated.GetAdditionalDocument(id) is not TextDocument document)
						continue;

					projects.Add(id.ProjectId);
					SourceText text = await document.GetTextAsync(token);
					if (!string.Equals(text.ToString(), diskText, StringComparison.Ordinal))
						updated = updated.WithAdditionalDocumentText(id, SourceText.From(diskText, text.Encoding));
				}

				// The instance may have been rebuilt since the event was classified; without a generation for the
				// projects now loaded, fall back to a reload.
				if (Instance.Workspace?.Razor is not RazorGenerationState razor || !projects.All(razor.Covers))
				{
					Instance.MarkDirty();
					return new WriteResult(updated, []);
				}

				updated = await RazorDocumentGenerator.RegenerateAsync(updated, projects, razor, token);
				return new WriteResult(updated, []);
			},
			async (solution, token) => await DiagnosticsService.GetAllDiagnosticsAsync(solution, includeAnalyzers: false, token),
			cancellationToken);
	}

	/// <summary>Reruns the Razor generator for projects whose generated code a C# change can affect.</summary>
	private async Task<Solution> RegenerateRazorAsync(Solution solution, IEnumerable<ProjectId> changedProjects, CancellationToken cancellationToken) =>
		Instance.Workspace?.Razor is RazorGenerationState razor
			? await RazorDocumentGenerator.RegenerateAsync(solution, changedProjects, razor, cancellationToken)
			: solution;

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

		await Instance.EnqueueWriteWithAutoDiagnosticsAsync(
			async (current, token) =>
			{
				SourceText newText = SourceText.From(diskText);
				Solution updated = current;
				var declarationsChanged = new HashSet<ProjectId>();
				foreach (DocumentId id in current.GetDocumentIdsWithFilePath(path))
				{
					if (current.GetDocument(id) is not Document before)
						continue;

					updated = updated.WithDocumentText(id, newText);

					// Generated Razor code depends only on declarations (types, members, attributes), never on
					// method bodies or initializers, so an edit confined to those cannot change it.
					if (await before.GetSyntaxTreeAsync(token) is not SyntaxTree oldTree
						|| await updated.GetDocument(id)!.GetSyntaxTreeAsync(token) is not SyntaxTree newTree
						|| !newTree.IsEquivalentTo(oldTree, topLevel: true))
					{
						declarationsChanged.Add(id.ProjectId);
					}
				}

				updated = await RegenerateRazorAsync(updated, declarationsChanged, token);
				return new WriteResult(updated, []);
			},
			async (solution, token) => await DiagnosticsService.GetAllDiagnosticsAsync(solution, includeAnalyzers: false, token),
			cancellationToken);
	}

	private async Task FoldRemoveAsync(string path, CancellationToken cancellationToken)
	{
		await Instance.EnqueueWriteAsync(async (current, token) =>
		{
			Solution updated = current;
			var projects = new HashSet<ProjectId>();
			foreach (DocumentId id in current.GetDocumentIdsWithFilePath(path))
			{
				updated = updated.RemoveDocument(id);
				projects.Add(id.ProjectId);
			}

			updated = await RegenerateRazorAsync(updated, projects, token);
			return new WriteResult(updated, []);
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

		await Instance.EnqueueWriteAsync(async (current, token) =>
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

			updated = await RegenerateRazorAsync(updated, projects, token);
			return new WriteResult(updated, []);
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

	private static IReadOnlyList<string> CaptureProjectDirectories(Solution solution) =>
		[.. solution.Projects
			.Select(project => project.FilePath is null ? null : System.IO.Path.GetDirectoryName(project.FilePath))
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// True for a path inside a dot-folder (.git, .vs, .idea) or node_modules below a project directory: the
	/// SDK's default item globs exclude both (<c>**/.*/**</c>, <c>**/node_modules/**</c>), and git, IDEs and
	/// package managers write there constantly. Only folders below the project directory count, so a solution
	/// that itself lives under a dot-folder is unaffected.
	/// </summary>
	private bool IsInIgnoredProjectFolder(string path)
	{
		foreach (string projectDirectory in ProjectDirectories)
		{
			if (!path.StartsWith(projectDirectory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				continue;

			string[] segments = System.IO.Path.GetRelativePath(projectDirectory, path)
				.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
			for (int index = 0; index < segments.Length - 1; index++)
			{
				if (segments[index].StartsWith('.') || segments[index].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
					return true;
			}
		}

		return false;
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
		if (IsAtomicWriteArtifact(path))
			return true;

		foreach (string segment in path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
		{
			if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	/// <summary>A staging sibling of an <see cref="AtomicFileWriter"/> commit: the server's own write
	/// activity, never a user edit. Suppressed here rather than in the watcher so every event route
	/// (including a direct call) agrees.</summary>
	private static bool IsAtomicWriteArtifact(string path) =>
		path.EndsWith(AtomicFileWriter.TempFileSuffix, StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(AtomicFileWriter.BackupFileSuffix, StringComparison.OrdinalIgnoreCase);

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
