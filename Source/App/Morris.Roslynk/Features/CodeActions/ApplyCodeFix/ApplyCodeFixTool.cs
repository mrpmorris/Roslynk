using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using System.Collections.Immutable;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.CodeActions.ApplyCodeFix;

[McpServerToolType]
public sealed class ApplyCodeFixTool
{
	public const string ApplyCodeFixName = "apply_code_fix";

	private static readonly ImmutableHashSet<string> UnnecessaryImportIds =
		ImmutableHashSet.Create(StringComparer.Ordinal, "CS8019", "IDE0005");

	private readonly InstanceRegistry InstanceRegistry;
	private readonly CodeActionService CodeActionService;
	private readonly ApplyPipeline ApplyPipeline;
	private readonly DocumentDiagnosticsProvider DocumentDiagnostics;

	public ApplyCodeFixTool(
		InstanceRegistry instanceRegistry,
		CodeActionService codeActionService,
		ApplyPipeline applyPipeline,
		DocumentDiagnosticsProvider documentDiagnostics)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
		DocumentDiagnostics = documentDiagnostics ?? throw new ArgumentNullException(nameof(documentDiagnostics));
	}

	[McpServerTool(
		Name = ApplyCodeFixName,
		Title = "Apply a diagnostic's fix",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		$"""
		Applies the code fix for the first occurrence of a diagnostic id (a compiler id such as CS0219, or an
		analyzer id such as IDE0005) in a .cs, .razor or .cshtml file; the
		quick path when you already know which diagnostic to clear, without first listing actions. Errors: a
		documentPath that is not a solution-compiled .cs, .razor or .cshtml document, or no such diagnostic in
		the file, is error=NotFound; a diagnostic with no registered fix is error=NotSupported; a fix that
		produced no changes is error=Conflict. In a .razor/.cshtml file the fix is computed on the generated C#
		and mapped back to the Razor source: an unmappable edit is error=NotSupported, mismatched Razor text
		error=Conflict, nothing written. Analyzers do not run on Razor-generated code, so Razor supports
		compiler diagnostics, plus CS8019/IDE0005, which remove the file's first unnecessary @using line. A file
		edited on disk since it was loaded is error=Stale. Returns a
		text result, not JSON: 'applied', 'action', 'status' header, a blank line, then one
		solution-relative changed-file path per line. {OutlineDescriptions.Project} {OutlineDescriptions.Freshness} Written atomically through the same safe write path. Pass
		checkOnly to preview without writing. Prefer this over hand-editing the file to clear a diagnostic so
		the in-memory model stays in sync.
		""")]
	public async Task<string> ApplyCodeFix(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path of the .cs, .razor or .cshtml file; absolute, or relative to the solution folder.")] string documentPath,
		[Description("The diagnostic id to fix, e.g. CS0219 or IDE0005.")] string diagnosticId,
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

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, documentPath, cancellationToken);
		if (source is null)
			return Failure(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs, .razor or .cshtml document."));
		Document document = source.Document;

		string title;
		Solution? changed;
		if (source.IsRazor && UnnecessaryImportIds.Contains(diagnosticId))
		{
			// The compiler reports neither id in generated code, so Razor @using lines get their own analysis.
			(changed, int removed) = await RazorUnusedUsings.RemoveAsync(solution, source, firstOnly: true, cancellationToken);
			if (removed == 0)
				return Failure(Error.NotFound($"No {diagnosticId} diagnostic was found in '{documentPath}'."));
			title = "Remove unnecessary usings";
		}
		else
		{
			ImmutableArray<Diagnostic> diagnostics = await DocumentDiagnostics.GetForDocumentAsync(document, cancellationToken);
			Diagnostic? diagnostic = diagnostics
				.Where(candidate => candidate.Id == diagnosticId && source.MapsToSource(candidate.Location))
				.OrderBy(candidate => candidate.Location.SourceSpan.Start)
				.FirstOrDefault();
			if (diagnostic is null)
				return Failure(Error.NotFound($"No {diagnosticId} diagnostic was found in '{documentPath}'."));

			TextSpan span = diagnostic.Location.SourceSpan;
			IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);
			DiscoveredAction? fix = actions.FirstOrDefault(action => action.DiagnosticId == diagnosticId);
			if (fix is null)
				return Failure(Error.NotSupported($"No fix is available for {diagnosticId}."));

			changed = await CodeActionService.ChangedSolutionAsync(fix.Action, cancellationToken);
			if (changed is null)
				return Failure(Error.Conflict("The fix produced no changes."));
			title = fix.Action.Title;

			try
			{
				changed = await RazorGeneratedChangeFolder.FoldAsync(solution, changed, cancellationToken);
			}
			catch (RazorMappingException exception)
			{
				return Failure(RazorGeneratedChangeFolder.ErrorFor(exception));
			}
		}

		IReadOnlyList<string> files;
		if (checkOnly)
		{
			files = ApplyPipeline.GetChangedFilePaths(solution, changed);
		}
		else
		{
			try
			{
				files = await ApplyPipeline.ApplyAsync(instance, changed, basedOn: solution, cancellationToken);
			}
			catch (StaleWriteException exception)
			{
				return OutlineError.Format(
					Error.Stale(exception.Message, [SolutionRelativePath.Of(solutionDirectory, exception.FilePath) ?? exception.FilePath]),
					instance.CurrentModel.Status);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("applied", !checkOnly);
		builder.Header("action", title);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, files, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}
}
