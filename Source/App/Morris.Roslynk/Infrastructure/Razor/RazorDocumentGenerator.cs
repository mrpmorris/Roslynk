using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Produces the Razor <c>.g.cs</c> documents as ordinary, editable documents of each Razor project. The SDK's
/// Razor source generator may target a newer Roslyn than Roslynk loads, in which case the workspace's analyzer
/// loader refuses it and emits nothing, leaving component partials (the <c>ComponentBase</c> base of every
/// <c>.razor</c>) out of the compilation and the hand-written <c>.razor.cs</c> code-behind riddled with phantom
/// CS0103/CS0115/CS0246. Even when it loads, its output would be immutable source-generated documents that
/// rename and the Razor mapping cannot edit. So Roslynk drives the generator itself (see
/// <see cref="RazorGeneratorLoader"/>) and adds the generated sources as documents. Best-effort: if the
/// generator cannot be loaded or run, the project is returned unchanged.
/// <para>
/// A fresh snapshot of pre-generated <c>.g.cs</c> files from a previous <c>dotnet build</c> is used instead of
/// running the generator (see <see cref="RazorSnapshot"/>). An invalid snapshot falls back to the generator;
/// when that is unavailable the snapshot minus provable orphans is used (stale files are kept — an outdated
/// partial binds more references than a missing one).
/// </para>
/// <para>
/// The generator's <see cref="GeneratorDriver"/> is kept in a <see cref="RazorGenerationState"/>, so after a
/// .razor or C# edit <see cref="RegenerateAsync"/> reruns it incrementally and replaces only the generated
/// documents whose text changed, without reloading the solution.
/// </para>
/// </summary>
public static class RazorDocumentGenerator
{
	private const string GeneratedFolder = "RoslynkRazorGenerated";

	/// <summary>
	/// Adds the generated Razor documents for every Razor project in the solution, returning the augmented
	/// solution and recording each project's generation in <paramref name="state"/>.
	/// </summary>
	public static async Task<Solution> AugmentAsync(Solution solution, RazorGenerationState state, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));
		if (state is null)
			throw new ArgumentNullException(nameof(state));

		// Dependencies first: a project's generator discovers the components of the projects it references from
		// their compilations, so theirs must already hold their generated partials. Solution order is not enough:
		// a page project listed before its component library would see the library's components as plain HTML.
		foreach (ProjectId projectId in solution.GetProjectDependencyGraph().GetTopologicallySortedProjects(cancellationToken))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (solution.GetProject(projectId) is Project project && HasRazorInputs(project))
				solution = await AugmentProjectAsync(solution, project, state, cancellationToken);
		}

		return solution;
	}

	/// <summary>
	/// Regenerates the Razor documents of <paramref name="changedProjects"/> and of every project depending on
	/// them that Roslynk generates for, dependencies first. Used after a .razor or C# edit has been applied to
	/// <paramref name="solution"/>; a project <paramref name="state"/> does not cover is left alone.
	/// </summary>
	public static async Task<Solution> RegenerateAsync(Solution solution, IEnumerable<ProjectId> changedProjects, RazorGenerationState state, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));
		if (state is null)
			throw new ArgumentNullException(nameof(state));

		HashSet<ProjectId> affected = AffectedProjects(solution, changedProjects, state);
		if (affected.Count == 0)
			return solution;

		foreach (ProjectId projectId in solution.GetProjectDependencyGraph().GetTopologicallySortedProjects(cancellationToken))
		{
			if (affected.Contains(projectId))
				solution = await RegenerateProjectAsync(solution, projectId, state, cancellationToken);
		}

		return solution;
	}

	/// <summary>True for a .razor or .cshtml path.</summary>
	public static bool IsRazorSourcePath(string path) =>
		path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	private static HashSet<ProjectId> AffectedProjects(Solution solution, IEnumerable<ProjectId> changedProjects, RazorGenerationState state)
	{
		ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
		var affected = new HashSet<ProjectId>();
		foreach (ProjectId projectId in changedProjects)
		{
			if (state.Covers(projectId))
				affected.Add(projectId);

			foreach (ProjectId dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(projectId))
			{
				if (state.Covers(dependent))
					affected.Add(dependent);
			}
		}

		return affected;
	}

	private static async Task<Solution> AugmentProjectAsync(Solution solution, Project project, RazorGenerationState state, CancellationToken cancellationToken)
	{
		string? snapshotDirectory = RazorSnapshot.DirectoryFor(project);
		RazorSnapshot.Analysis? snapshot = snapshotDirectory is null
			? null
			: RazorSnapshot.Analyze(solution, project, snapshotDirectory);

		// Resolved before the native reference is removed: the project's own reference is the preferred source.
		ISourceGenerator? generator = RazorGeneratorLoader.For(project);
		string generatedRoot = GeneratedRoot(project);

		if (snapshot is { IsFresh: true })
		{
			(Solution withSnapshot, ImmutableDictionary<string, DocumentId> documents) =
				await AddPreGeneratedFilesAsync(solution, project.Id, snapshot.Files, cancellationToken);
			if (!documents.IsEmpty)
			{
				// With a generator at hand, a later edit regenerates from this snapshot's documents in place.
				if (generator is not null)
					state.Set(project.Id, new RazorGenerationState.Entry(generator, Driver: null, generatedRoot, documents, Diagnostics: [], InputCompilations: null));
				return RemoveNativeRazorGenerator(withSnapshot, project.Id);
			}
		}

		if (generator is not null)
		{
			state.Set(project.Id, new RazorGenerationState.Entry(generator, Driver: null, generatedRoot, ImmutableDictionary<string, DocumentId>.Empty, Diagnostics: [], InputCompilations: null));
			return await RegenerateProjectAsync(solution, project.Id, state, cancellationToken);
		}

		if (snapshot is not null)
		{
			// No generator to fall back to: use the snapshot minus provable orphans. Stale files are
			// kept deliberately — an outdated component partial still declares the class and its
			// members, so keeping it binds far more references than omitting it; only files whose
			// source is gone poison the compilation with symbols that no longer exist anywhere.
			(Solution withSnapshot, ImmutableDictionary<string, DocumentId> documents) =
				await AddPreGeneratedFilesAsync(solution, project.Id, snapshot.Files, cancellationToken);
			if (!documents.IsEmpty)
				return RemoveNativeRazorGenerator(withSnapshot, project.Id);
		}

		return solution;
	}

	/// <summary>
	/// Runs the project's generator (incrementally, reusing the last driver) and brings its generated documents
	/// in line with the output: changed ones get new text, new ones are added, vanished ones removed.
	/// </summary>
	private static async Task<Solution> RegenerateProjectAsync(Solution solution, ProjectId projectId, RazorGenerationState state, CancellationToken cancellationToken)
	{
		if (state.Get(projectId) is not RazorGenerationState.Entry entry || solution.GetProject(projectId) is null)
			return solution;

		try
		{
			// Once our documents own the generation, the SDK generator must not also run natively: where it loads,
			// its source-generated copy of every component partial would duplicate ours (CS0102/CS0111) with
			// references binding to the immutable copy — breaking rename and diagnostics. Removing its reference
			// before compiling also keeps the compilation below from running the whole generator a second time.
			Solution stripped = RemoveNativeRazorGenerator(solution, projectId);
			Project project = stripped.GetProject(projectId)!;
			if (project.ParseOptions is not CSharpParseOptions parseOptions || await project.GetCompilationAsync(cancellationToken) is not Compilation compilation)
				return solution;

			Compilation input = await GeneratorInputAsync(project, compilation, entry, cancellationToken);
			GeneratorDriver driver = entry.Driver is null
				? CSharpGeneratorDriver.Create(
					generators: [entry.Generator],
					additionalTexts: project.AnalyzerOptions.AdditionalFiles,
					parseOptions: parseOptions,
					optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider)
				: entry.Driver
					.ReplaceAdditionalTexts(project.AnalyzerOptions.AdditionalFiles)
					.WithUpdatedParseOptions(parseOptions)
					.WithUpdatedAnalyzerConfigOptions(project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
			driver = driver.RunGenerators(input, cancellationToken);
			GeneratorDriverRunResult result = driver.GetRunResult();

			// A generator crash (reported as CS8785 among the diagnostics) produces no sources; keeping the previous
			// documents binds far more than dropping every component partial would.
			ImmutableArray<GeneratedSourceResult> sources = [.. result.Results.SelectMany(run => run.GeneratedSources)];
			if (result.Results.Any(run => run.Exception is not null) || (sources.IsEmpty && entry.Documents.IsEmpty))
			{
				state.Set(projectId, entry with { Driver = driver, Diagnostics = result.Diagnostics });
				return solution;
			}

			(Solution applied, ImmutableDictionary<string, DocumentId> documents) = await ApplyAsync(stripped, projectId, entry, sources, cancellationToken);

			// Remember which full compilation the generator's input was derived from: an edit that leaves the
			// compilation untouched (a .razor-only change) then hands the driver the identical input, and its
			// incremental pipeline skips component discovery entirely.
			Compilation? full = await applied.GetProject(projectId)!.GetCompilationAsync(cancellationToken);
			state.Set(projectId, entry with
			{
				Driver = driver,
				Documents = documents,
				Diagnostics = result.Diagnostics,
				InputCompilations = full is null ? null : (full, input),
			});
			return applied;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return solution;
		}
	}

	/// <summary>
	/// The compilation the generator sees: the project's, minus the generated Razor documents from a previous
	/// run — the build never feeds the generator its own output, and a stale partial left in would keep a removed
	/// parameter or component discoverable.
	/// </summary>
	private static async Task<Compilation> GeneratorInputAsync(Project project, Compilation compilation, RazorGenerationState.Entry entry, CancellationToken cancellationToken)
	{
		if (entry.InputCompilations is ({ } full, { } input) && ReferenceEquals(full, compilation))
			return input;

		var generatedTrees = new List<SyntaxTree>();
		foreach (DocumentId documentId in entry.Documents.Values)
		{
			if (project.GetDocument(documentId) is Document document
				&& await document.GetSyntaxTreeAsync(cancellationToken) is SyntaxTree tree
				&& compilation.ContainsSyntaxTree(tree))
			{
				generatedTrees.Add(tree);
			}
		}

		return generatedTrees.Count == 0 ? compilation : compilation.RemoveSyntaxTrees(generatedTrees);
	}

	/// <summary>
	/// Applies the generator's output to the project's generated documents, keyed by hint name: text changes
	/// only where it differs, new documents in one batch (adding them one at a time slows the next compilation
	/// several-fold on a large project), and documents whose output vanished removed.
	/// </summary>
	private static async Task<(Solution Solution, ImmutableDictionary<string, DocumentId> Documents)> ApplyAsync(
		Solution solution,
		ProjectId projectId,
		RazorGenerationState.Entry entry,
		ImmutableArray<GeneratedSourceResult> sources,
		CancellationToken cancellationToken)
	{
		var documents = ImmutableDictionary.CreateBuilder<string, DocumentId>(StringComparer.Ordinal);
		var added = ImmutableArray.CreateBuilder<DocumentInfo>();

		foreach (GeneratedSourceResult source in sources)
		{
			if (entry.Documents.TryGetValue(source.HintName, out DocumentId? existingId) && solution.GetDocument(existingId) is Document existing)
			{
				documents[source.HintName] = existingId;
				if (!(await existing.GetTextAsync(cancellationToken)).ContentEquals(source.SourceText))
					solution = solution.WithDocumentText(existingId, source.SourceText);
				continue;
			}

			// One path per hint name across a multi-targeted project's frameworks: every per-framework copy of a
			// generated file is the same file to the Razor mapping (RazorGeneratedChangeFolder folds it once).
			string generatedPath = System.IO.Path.Combine(entry.GeneratedRoot, source.HintName);
			DocumentId documentId = DocumentId.CreateNewId(projectId, source.HintName);
			documents[source.HintName] = documentId;
			added.Add(DocumentInfo.Create(
				documentId,
				source.HintName,
				loader: TextLoader.From(TextAndVersion.Create(source.SourceText, VersionStamp.Create(), generatedPath)),
				filePath: generatedPath));
		}

		ImmutableArray<DocumentId> removed = [.. entry.Documents
			.Where(pair => !documents.ContainsKey(pair.Key) && solution.GetDocument(pair.Value) is not null)
			.Select(pair => pair.Value)];
		if (!removed.IsEmpty)
			solution = solution.RemoveDocuments(removed);
		if (added.Count > 0)
			solution = solution.AddDocuments(added.ToImmutable());

		return (solution, documents.ToImmutable());
	}

	private static async Task<(Solution Solution, ImmutableDictionary<string, DocumentId> Documents)> AddPreGeneratedFilesAsync(
		Solution solution,
		ProjectId projectId,
		IReadOnlyList<(string Path, string HintName)> files,
		CancellationToken cancellationToken)
	{
		var documents = ImmutableDictionary.CreateBuilder<string, DocumentId>(StringComparer.Ordinal);
		var added = ImmutableArray.CreateBuilder<DocumentInfo>();

		try
		{
			foreach ((string file, string hintName) in files)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (solution.GetDocumentIdsWithFilePath(file).Any(id => id.ProjectId == projectId))
					continue;

				string content = await File.ReadAllTextAsync(file, cancellationToken);
				DocumentId documentId = DocumentId.CreateNewId(projectId, hintName);
				documents[hintName] = documentId;
				added.Add(DocumentInfo.Create(
					documentId,
					System.IO.Path.GetFileName(file),
					loader: TextLoader.From(TextAndVersion.Create(SourceText.From(content), VersionStamp.Create(), file)),
					filePath: file));
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			// A snapshot that cannot be read in full is not used at all.
			return (solution, ImmutableDictionary<string, DocumentId>.Empty);
		}

		return added.Count == 0
			? (solution, documents.ToImmutable())
			: (solution.AddDocuments(added.ToImmutable()), documents.ToImmutable());
	}

	private static Solution RemoveNativeRazorGenerator(Solution solution, ProjectId projectId)
	{
		if (solution.GetProject(projectId) is not Project project)
			return solution;

		foreach (AnalyzerReference reference in project.AnalyzerReferences)
		{
			if (RazorGeneratorLoader.IsRazorCompiler(reference))
				solution = solution.RemoveAnalyzerReference(projectId, reference);
		}

		return solution;
	}

	/// <summary>
	/// Where the generated documents claim to live. Nothing is written there: the path only has to be stable,
	/// shared by a multi-targeted project's frameworks, and recognisable to <see cref="RazorMapping"/>.
	/// </summary>
	private static string GeneratedRoot(Project project)
	{
		string projectDirectory = project.FilePath is string filePath
			? System.IO.Path.GetDirectoryName(filePath)!
			: AppContext.BaseDirectory;
		return System.IO.Path.Combine(projectDirectory, "obj", GeneratedFolder);
	}

	/// <summary>
	/// Whether the project has Razor sources the generator can see. The generator reads only the analyzer
	/// additional files, so .razor/.cshtml files on disk that are not additional documents produce nothing.
	/// </summary>
	private static bool HasRazorInputs(Project project) =>
		project.AdditionalDocuments.Any(document => document.FilePath is string path && IsRazorSourcePath(path));
}
