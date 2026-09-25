using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.Usings.RemoveUnusedUsings;

/// <summary>
/// Removes unnecessary using directives. The compiler's CS8019 finds the files worth touching, then each
/// one is rewritten by Roslyn's own IDE0005 fix, which gets the surrounding trivia right. Where that fix is
/// unavailable - IDE0005's analyzer only ships with <c>EnforceCodeStyleInBuild</c> - the directives are
/// removed syntactically instead, keeping their leading trivia so comments above a using survive.
/// </summary>
[McpServerToolType]
public sealed class RemoveUnusedUsingsTool
{
	public const string RemoveUnusedUsingsName = "remove_unused_usings";

	private const string UnnecessaryUsingId = "CS8019";

	/// <summary>IDE0005 and its generated-code counterpart, both fixed by the same provider.</summary>
	private static readonly ImmutableHashSet<string> UnnecessaryImportIds =
		ImmutableHashSet.Create(StringComparer.Ordinal, "IDE0005", "IDE0005_gen");

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ApplyPipeline ApplyPipeline;
	private readonly CodeActionService CodeActionService;
	private readonly DocumentDiagnosticsProvider DocumentDiagnostics;

	public RemoveUnusedUsingsTool(
		InstanceRegistry instanceRegistry,
		ApplyPipeline applyPipeline,
		CodeActionService codeActionService,
		DocumentDiagnosticsProvider documentDiagnostics)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
		DocumentDiagnostics = documentDiagnostics ?? throw new ArgumentNullException(nameof(documentDiagnostics));
	}

	[McpServerTool(
		Name = RemoveUnusedUsingsName,
		Title = "Remove unused usings",
		ReadOnly = false,
		Idempotent = true,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		$"""
		Removes unnecessary using directives (the compiler's CS8019) across the solution, or in one file when
		documentPath is given; the recurring cleanup after moves and renames. .razor and .cshtml files are
		included: an unnecessary @using line written in the file is removed, while directives from imports files
		(_Imports.razor, _ViewImports.cshtml) are left alone because other components share them. A documentPath
		that is not a solution-compiled .cs, .razor or .cshtml document is error=NotFound; a file edited on disk
		since it was loaded is error=Stale; if there is nothing to remove the call still
		succeeds with applied=N and removedCount=0 (not an error, and safe to re-run). Returns a text result, not JSON:
		'applied', 'removedCount', 'status' header, a blank line, then one solution-relative
		changed-file path per line. {OutlineDescriptions.Project} {OutlineDescriptions.Freshness} Written atomically through the same safe write path as the other tools. Pass
		checkOnly to preview the changed files without writing.
		""")]
	public async Task<string> RemoveUnusedUsings(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Optional path of a single .cs, .razor or .cshtml file to clean (absolute or relative to the solution folder). Omit to clean the whole solution.")] string? documentPath = null,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		string Success(bool applied, IReadOnlyList<string> changed, int removedCount)
		{
			var builder = new OutlineBuilder();
			builder.Header("applied", applied);
			builder.Header("removedCount", removedCount);
			builder.Status(instance.CurrentModel.Status);
			ChangedFilesOutline.Write(builder, changed, instance.CurrentSolution, solutionDirectory);
			return builder.ToString();
		}

		HashSet<DocumentId>? targetDocuments = null;
		RazorSourceDocument? targetRazor = null;
		if (documentPath is not null)
		{
			RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, documentPath, cancellationToken);
			if (source is null)
				return Failure(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs, .razor or .cshtml document."));
			if (source.IsRazor)
			{
				targetRazor = source;
				targetDocuments = [];
			}
			else
			{
				targetDocuments = [source.Document.Id];
			}
		}

		Solution updated = solution;
		int removed = 0;
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			// CS8019 only picks the files worth rewriting; the rewrite itself is the IDE0005 fix below.
			IEnumerable<IGrouping<SyntaxTree, Diagnostic>> byTree = compilation.GetDiagnostics(cancellationToken)
				.Where(diagnostic => diagnostic.Id == UnnecessaryUsingId && diagnostic.Location.SourceTree is not null)
				.GroupBy(diagnostic => diagnostic.Location.SourceTree!);

			foreach (IGrouping<SyntaxTree, Diagnostic> treeDiagnostics in byTree)
			{
				Document? document = solution.GetDocument(treeDiagnostics.Key);
				if (document is null || !IsEditableSource(document) || (targetDocuments is not null && !targetDocuments.Contains(document.Id)))
					continue;

				Document? current = updated.GetDocument(document.Id);
				if (current is null)
					continue;

				int before = await UsingCountAsync(current, cancellationToken);
				Solution? fixedSolution = await TryFixAsync(current, cancellationToken)
					?? await RemoveByHandAsync(updated, current, treeDiagnostics, cancellationToken);
				if (fixedSolution is null)
					continue;

				updated = fixedSolution;
				Document? rewritten = updated.GetDocument(document.Id);
				int after = rewritten is null ? before : await UsingCountAsync(rewritten, cancellationToken);
				removed += Math.Max(before - after, 0);
			}
		}

		// Razor-generated code never reports CS8019, so .razor/.cshtml files get their own @using analysis.
		IEnumerable<RazorSourceDocument> razorSources = targetRazor is not null
			? [targetRazor]
			: documentPath is null ? await RazorSourcesAsync(solution, cancellationToken) : [];
		foreach (RazorSourceDocument razorSource in razorSources)
		{
			(updated, int razorRemoved) = await RazorUnusedUsings.RemoveAsync(updated, razorSource, firstOnly: false, cancellationToken);
			removed += razorRemoved;
		}

		if (removed == 0)
			return Success(applied: false, [], 0);

		if (checkOnly)
			return Success(applied: false, ApplyPipeline.GetChangedFilePaths(solution, updated), removed);

		IReadOnlyList<string> changed;
		try
		{
			changed = await ApplyPipeline.ApplyAsync(instance, updated, basedOn: solution, cancellationToken);
		}
		catch (StaleWriteException exception)
		{
			return OutlineError.Format(
				Error.Stale(exception.Message, [SolutionRelativePath.Of(solutionDirectory, exception.FilePath) ?? exception.FilePath]),
				instance.CurrentModel.Status);
		}
		return Success(applied: true, changed, removed);
	}

	/// <summary>Every compiled .razor/.cshtml file in the solution (once per path), excluding imports files.</summary>
	private static async Task<IReadOnlyList<RazorSourceDocument>> RazorSourcesAsync(Solution solution, CancellationToken cancellationToken)
	{
		var sources = new List<RazorSourceDocument>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (TextDocument additional in solution.Projects.SelectMany(project => project.AdditionalDocuments))
		{
			if (additional.FilePath is not string path || !RazorSourceDocument.IsRazorSourcePath(path) || RazorUnusedUsings.IsImportsFile(path) || !seen.Add(path))
				continue;

			if (await RazorSourceDocument.ResolveAsync(solution, path, cancellationToken) is RazorSourceDocument source)
				sources.Add(source);
		}

		return sources;
	}

	/// <summary>
	/// The solution Roslyn's IDE0005 fix would produce for the whole document, or null when the analyzer
	/// that reports IDE0005 is not referenced by the project.
	/// </summary>
	private async Task<Solution?> TryFixAsync(Document document, CancellationToken cancellationToken)
	{
		ImmutableArray<Diagnostic> diagnostics = await DocumentDiagnostics.GetForDocumentAsync(document, cancellationToken);
		foreach (Diagnostic diagnostic in diagnostics.Where(candidate => UnnecessaryImportIds.Contains(candidate.Id)))
		{
			IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, diagnostic.Location.SourceSpan, cancellationToken);
			DiscoveredAction? fix = actions.FirstOrDefault(action => action.DiagnosticId == diagnostic.Id);
			if (fix is null)
				continue;

			Solution? changed = await CodeActionService.ChangedSolutionAsync(fix.Action, cancellationToken);
			if (changed is not null)
				return changed;
		}

		return null;
	}

	/// <summary>
	/// Removes the CS8019 directives syntactically. Their leading trivia is kept so a comment or a
	/// conditional directive above a using is not taken with it.
	/// </summary>
	private static async Task<Solution?> RemoveByHandAsync(
		Solution solution,
		Document document,
		IEnumerable<Diagnostic> treeDiagnostics,
		CancellationToken cancellationToken)
	{
		SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken);
		if (root is null)
			return null;

		UsingDirectiveSyntax[] usings = treeDiagnostics
			.Select(diagnostic => root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>())
			.Where(node => node is not null)
			.Distinct()
			.ToArray()!;

		if (usings.Length == 0)
			return null;

		SyntaxNode newRoot = root.RemoveNodes(usings, SyntaxRemoveOptions.KeepUnbalancedDirectives | SyntaxRemoveOptions.KeepLeadingTrivia)!;
		return solution.WithDocumentSyntaxRoot(document.Id, newRoot);
	}

	private static async Task<int> UsingCountAsync(Document document, CancellationToken cancellationToken)
	{
		SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken);
		return root is null ? 0 : root.DescendantNodes().OfType<UsingDirectiveSyntax>().Count();
	}

	private static readonly string[] GeneratedSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs"];

	/// <summary>An on-disk source file we may rewrite; never a generated or obj/bin document.</summary>
	private static bool IsEditableSource(Document document)
	{
		string? path = document.FilePath;
		if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return false;
		if (GeneratedSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
			return false;

		foreach (string segment in path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
		{
			if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return true;
	}
}
