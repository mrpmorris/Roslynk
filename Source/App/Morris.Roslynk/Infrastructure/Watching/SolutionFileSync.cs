using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
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
	/// already matches). An edit to a known document or additional document (a .razor source or a source
	/// generator's input) is folded into the snapshot in place, as are a deleted compiled document and a new
	/// .cs file a default glob owns; every change in one debounced batch is folded as a single write, whose
	/// Razor regeneration the instance performs. A project / props / sln file change, or a file that may have
	/// joined or left the build, marks the instance dirty so the registry reloads it on next use. Files that
	/// cannot reach the compiler (a tool's output written beside the sources) are ignored. This is a freshness
	/// optimization, not a correctness mechanism; the
	/// apply pipeline's stale-write guard is what actually protects the user, so a missed event only costs a
	/// stale read until the next one.
	/// <para>
	/// A file whose content did not change (an editor save with identical bytes, or the server's own write)
	/// needs nothing. Files inside a project's dot-folders (.git, .vs, .idea) or node_modules are noise the
	/// SDK's own item globs exclude; only build files there still count.
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
			".ruleset",
			".globalconfig",
		};

		/// <summary>Build inputs recognised by name: analyzer config, the SDK pin and the package sources.</summary>
		private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
		{
			".editorconfig",
			"global.json",
			"NuGet.config",
		};

		/// <summary>Extensions a new file joins the build with by the SDK's own globs (Razor sources as additional
		/// files); .cs and the build files are handled on their own.</summary>
		private static readonly HashSet<string> BuildInputExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".razor",
			".cshtml",
		};

		private static readonly string[] AncestorBuildFileNames =
		[
			"Directory.Build.props",
			"Directory.Build.targets",
			"Directory.Packages.props",
			".editorconfig",
			"global.json",
			"NuGet.config",
		];

		private readonly RoslynInstance Instance;

		/// <summary>The hash of each build file as it was on disk at load, so we can tell a real edit from a touch.</summary>
		private readonly ConcurrentDictionary<string, string> BuildFileBaseline;

		/// <summary>Whether each project file uses default compile globs, so a new .cs can be folded in vs reloaded.</summary>
		private readonly ConcurrentDictionary<string, bool> UsesDefaultCompileItemsCache = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>The project directories at load, under which dot-folders and node_modules are ignored.</summary>
		private readonly IReadOnlyList<string> ProjectDirectories;

public SolutionFileSync(RoslynInstance instance)
		{
			Instance = instance ?? throw new ArgumentNullException(nameof(instance));
			BuildFileBaseline = CaptureBuildFileHashes(instance.CurrentSolution);
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
	public Task OnFileChangedAsync(string path, CancellationToken cancellationToken = default) =>
		OnFilesChangedAsync([path], cancellationToken);

	/// <summary>
	/// Reacts to one debounced batch of changed paths. Every edit, addition and removal in the batch is folded
	/// into the snapshot as one write, so a save-all or a branch switch touching many files compiles and
	/// regenerates Razor once rather than once per file. Safe to call for any path; noise is ignored.
	/// </summary>
	public async Task OnFilesChangedAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
	{
		var folds = new List<Fold>();
		foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (await ClassifyAsync(path, cancellationToken) is Fold fold)
				folds.Add(fold);
		}

		if (folds.Count > 0)
			await Instance.EnqueueWriteAsync(async (current, token) => new WriteResult(await FoldAsync(current, folds, token), []), cancellationToken);
	}

	/// <summary>A change the snapshot can absorb in place.</summary>
	private abstract record Fold(string Path);

	/// <summary>New content for every document and additional document at the path.</summary>
	private sealed record TextFold(string Path, string Text) : Fold(Path);

	/// <summary>A compiled document that was deleted.</summary>
	private sealed record RemoveFold(string Path) : Fold(Path);

	/// <summary>A new .cs file the default compile glob of each of <paramref name="Projects"/> includes.</summary>
	private sealed record AddFold(string Path, string Text, IReadOnlyList<ProjectId> Projects) : Fold(Path);

	/// <summary>
	/// Decides what a changed path means: a <see cref="Fold"/> the snapshot can absorb, or nothing (noise, an
	/// unchanged file, or a change only a reload can absorb, in which case the instance is marked dirty).
	/// </summary>
	private async Task<Fold?> ClassifyAsync(string path, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(path) || IsIgnored(path))
			return null;

		if (IsBuildFile(path))
		{
			OnBuildFileChanged(path);
			return null;
		}

		if (IsInIgnoredProjectFolder(path))
			return null;

		Solution current = Instance.CurrentSolution;
		ImmutableArray<DocumentId> ids = [.. current.GetDocumentIdsWithFilePath(path)
			.Where(id => current.GetDocument(id) is not null || current.GetAdditionalDocument(id) is not null)];
		bool exists = File.Exists(path);

		if (!ids.IsEmpty && exists)
			return await ClassifyEditAsync(current, path, ids, cancellationToken);

		if (!ids.IsEmpty)
		{
			// Deleted. A compiled document is dropped in place; an additional file leaves the build in ways only
			// MSBuild can tell (a Razor page, a generator input), so that reloads.
			if (ids.All(id => current.GetDocument(id) is not null))
				return new RemoveFold(path);

			Instance.MarkDirty();
			return null;
		}

		if (IsSourceFile(path))
		{
			// A new .cs file joins every project whose directory owns it and uses default compile globs; otherwise
			// its membership is MSBuild's call. A deleted unknown .cs file needs nothing.
			if (!exists)
				return null;
			if (TryFindDefaultGlobProjects(current, path, out IReadOnlyList<ProjectId> projects) && await TryReadAsync(path, cancellationToken) is string text)
				return new AddFold(path, text, projects);

			Instance.MarkDirty();
			return null;
		}

		// A path the model does not hold. Only a change of build membership matters, and only MSBuild can
		// evaluate that, so mark the instance dirty and let the next read reload. MarkDirty is lazy and
		// idempotent, so even a burst of such changes costs at most one reload.
		if (CouldChangeBuildInputs(path))
			Instance.MarkDirty();
		return null;
	}

	/// <summary>
	/// An existing known file was written. Unchanged content (the server's own write, an identical save) needs
	/// nothing. A Razor source of a project whose Razor output Roslynk does not generate reloads, since nothing
	/// else would regenerate it; anything else is folded in place.
	/// </summary>
	private async Task<Fold?> ClassifyEditAsync(Solution current, string path, ImmutableArray<DocumentId> ids, CancellationToken cancellationToken)
	{
		if (await TryReadAsync(path, cancellationToken) is not string diskText || !await DiffersAsync(current, ids, diskText, cancellationToken))
			return null;

		if (RazorDocumentGenerator.IsRazorSourcePath(path) && !CoversRazorOf(ids))
		{
			Instance.MarkDirty();
			return null;
		}

		return new TextFold(path, diskText);
	}

	private bool CoversRazorOf(IEnumerable<DocumentId> ids) =>
		Instance.Workspace?.Razor is RazorGenerationState razor && ids.All(id => razor.Covers(id.ProjectId));

	private static async Task<bool> DiffersAsync(Solution solution, IEnumerable<DocumentId> ids, string text, CancellationToken cancellationToken)
	{
		foreach (DocumentId id in ids)
		{
			if (TextDocumentOf(solution, id) is TextDocument document && !string.Equals((await document.GetTextAsync(cancellationToken)).ToString(), text, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Applies a batch of folds to the latest snapshot. Each text is compared against that snapshot rather than
	/// the one the event was classified against: a write published since (the event raced the server's own
	/// write) may already hold it, and replacing a text with an equal one would still fork every compilation
	/// downstream.
	/// </summary>
	private async Task<Solution> FoldAsync(Solution current, IReadOnlyList<Fold> folds, CancellationToken cancellationToken)
	{
		Solution updated = current;
		foreach (Fold fold in folds)
		{
			switch (fold)
			{
				case TextFold textFold:
					if (RazorDocumentGenerator.IsRazorSourcePath(fold.Path) && !CoversRazorOf(updated.GetDocumentIdsWithFilePath(fold.Path)))
					{
						// Rebuilt since the event was classified, without Roslynk generating these projects' Razor.
						Instance.MarkDirty();
						continue;
					}

					foreach (DocumentId id in updated.GetDocumentIdsWithFilePath(fold.Path))
					{
						if (TextDocumentOf(updated, id) is not TextDocument document)
							continue;

						SourceText text = await document.GetTextAsync(cancellationToken);
						if (string.Equals(text.ToString(), textFold.Text, StringComparison.Ordinal))
							continue;

						SourceText newText = SourceText.From(textFold.Text, text.Encoding);
						if (updated.GetDocument(id) is not null)
							updated = updated.WithDocumentText(id, newText);
						else if (updated.GetAdditionalDocument(id) is not null)
							updated = updated.WithAdditionalDocumentText(id, newText);
					}
					break;

				case RemoveFold:
					foreach (DocumentId id in updated.GetDocumentIdsWithFilePath(fold.Path))
					{
						if (updated.GetDocument(id) is not null)
							updated = updated.RemoveDocument(id);
					}
					break;

				case AddFold addFold:
					foreach (ProjectId projectId in addFold.Projects)
					{
						if (updated.GetProject(projectId) is null || updated.GetDocumentIdsWithFilePath(fold.Path).Any(id => id.ProjectId == projectId))
							continue;

						updated = updated.AddDocument(DocumentId.CreateNewId(projectId), System.IO.Path.GetFileName(fold.Path), SourceText.From(addFold.Text), filePath: fold.Path);
					}
					break;
			}
		}

		return updated;
	}

	/// <summary>The document or additional document <paramref name="id"/> names; analyzer config is not folded.</summary>
	private static TextDocument? TextDocumentOf(Solution solution, DocumentId id) =>
		(TextDocument?)solution.GetDocument(id) ?? solution.GetAdditionalDocument(id);

	/// <summary>The file's text, or null while an editor holds it locked (a later event catches up).</summary>
	private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			return await File.ReadAllTextAsync(path, cancellationToken);
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// True when a path the model does not hold could still change what the compiler sees. Roslyn sees only
	/// compile items, additional files and analyzer config; embedded resources, content and anything else never
	/// reach it (Roslynk never emits). The model already lists every input it loaded, so an unknown file is not
	/// one: it matters only when it is new and a glob would add it, which is assumed for the extensions the SDK
	/// globs (.razor/.cshtml) and those of the additional files already loaded. A loaded file, or a directory of
	/// loaded files, that went away matters too. A tool's output written beside the sources (a weaver's .csv, an
	/// IDE's state) therefore costs no reload.
	/// </summary>
	private bool CouldChangeBuildInputs(string path)
	{
		if (Directory.Exists(path))
			return !IsIrrelevantDirectory(path);

		Solution solution = Instance.CurrentSolution;
		if (!solution.GetDocumentIdsWithFilePath(path).IsEmpty)
			return true;

		string prefix = System.IO.Path.TrimEndingDirectorySeparator(path) + System.IO.Path.DirectorySeparatorChar;
		if (DocumentAndBuildFilePaths(solution).Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			return true;

		string extension = System.IO.Path.GetExtension(path);
		return BuildInputExtensions.Contains(extension)
			|| solution.Projects
				.SelectMany(project => project.AdditionalDocuments)
				.Any(document => string.Equals(System.IO.Path.GetExtension(document.FilePath), extension, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// True for an existing directory that cannot bring new build input. The watcher reports a directory's own
	/// creation and renaming (its contents raise no events of their own), so only a directory inside a project
	/// that is not a dot-folder or node_modules and holds no loaded documents yet reaches the reload; one that
	/// already holds loaded documents had its files folded one by one.
	/// </summary>
	private bool IsIrrelevantDirectory(string path)
	{
		string directory = System.IO.Path.TrimEndingDirectorySeparator(path);
		if (!ProjectDirectories.Any(projectDirectory => IsUnder(directory, projectDirectory)))
			return true;

		string name = System.IO.Path.GetFileName(directory);
		if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
			return true;

		string prefix = directory + System.IO.Path.DirectorySeparatorChar;
		return DocumentAndBuildFilePaths(Instance.CurrentSolution)
			.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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
		|| BuildFileNames.Contains(System.IO.Path.GetFileName(path));

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
