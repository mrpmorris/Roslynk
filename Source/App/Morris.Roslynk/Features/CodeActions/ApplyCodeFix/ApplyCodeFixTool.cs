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
		Applies the code fix for the diagnostic with this id (a compiler id such as CS0219, or an analyzer id
		such as IDE0005) at line:column in a .cs, .razor or .cshtml file; the quick path when get_diagnostics
		has already told you the id and position, without first listing actions. Pass the 1-based line and
		column get_diagnostics printed for that entry; the diagnostic whose span contains that position is
		fixed. When the diagnostic has more than one distinct fix (for example several namespaces to import,
		or make-nullable versus add-required), nothing is written and the result is error=Conflict with one
		'candidate=<actionId>,Fix,<diagnosticId> <title>' header per fix: choose the fix whose title matches
		your intent and pass its actionId to apply_code_action; do not call apply_code_fix again for it.
		Errors: a documentPath that is not a solution-compiled .cs, .razor or .cshtml document, or no such
		diagnostic at that position, is error=NotFound; a line or column below 1 is error=Invalid; a diagnostic
		with no registered fix is error=NotSupported; a fix that produced no changes is error=Conflict (without
		candidates). In a .razor/.cshtml file the position is in the Razor file, the fix is computed on the
		generated C# and mapped back to the Razor source: an unmappable edit is error=NotSupported, mismatched
		Razor text error=Conflict, nothing written. Analyzers do not run on Razor-generated code, so Razor
		supports compiler diagnostics, plus CS8019/IDE0005, which remove the unnecessary @using on that line.
		A file edited on disk since it was loaded is error=Stale. Returns a text result, not JSON: 'applied',
		'action', 'status' header, a blank line, then one solution-relative changed-file path per line.
		{OutlineDescriptions.Project} {OutlineDescriptions.Freshness} Written atomically through the same safe
		write path. Pass checkOnly to preview without writing. Prefer this over hand-editing the file to clear
		a diagnostic so the in-memory model stays in sync.
		""")]
	public async Task<string> ApplyCodeFix(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path of the .cs, .razor or .cshtml file; absolute, or relative to the solution folder.")] string documentPath,
		[Description("The diagnostic id to fix, e.g. CS0219 or IDE0005.")] string diagnosticId,
		[Description("1-based line of the diagnostic, as get_diagnostics reports it.")] int line,
		[Description("1-based column of the diagnostic, as get_diagnostics reports it.")] int column,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());
		if (line < 1 || column < 1)
			return Failure(Error.Invalid("line and column are 1-based and must be at least 1."));

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, documentPath, cancellationToken);
		if (source is null)
			return Failure(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs, .razor or .cshtml document."));
		Document document = source.Document;

		var position = new LinePosition(line - 1, column - 1);
		string notFound = $"No {diagnosticId} diagnostic was found at {line}:{column} in '{documentPath}'.";

		string title;
		Solution? changed;
		if (source.IsRazor && UnnecessaryImportIds.Contains(diagnosticId))
		{
			// The compiler reports neither id in generated code, so Razor @using lines get their own analysis.
			(changed, int removed) = await RazorUnusedUsings.RemoveAsync(solution, source, line: position.Line, cancellationToken);
			if (removed == 0)
				return Failure(Error.NotFound(notFound));
			title = "Remove unnecessary usings";
		}
		else
		{
			ImmutableArray<Diagnostic> diagnostics = await DocumentDiagnostics.GetForDocumentAsync(document, cancellationToken);
			Diagnostic? diagnostic = diagnostics
				.Where(candidate => candidate.Id == diagnosticId && source.MapsToSource(candidate.Location) && Contains(source, candidate.Location, position))
				.OrderBy(candidate => candidate.Location.SourceSpan.Length)
				.FirstOrDefault();
			if (diagnostic is null)
				return Failure(Error.NotFound(notFound));

			TextSpan span = diagnostic.Location.SourceSpan;
			DiscoveredAction[] fixes = (await CodeActionService.DiscoverAsync(document, span, cancellationToken))
				.Where(action => action.DiagnosticId == diagnosticId)
				.DistinctBy(action => CodeActionService.KeyOf(action.Action), StringComparer.Ordinal)
				.ToArray();
			if (fixes.Length == 0)
				return Failure(Error.NotSupported($"No fix is available for {diagnosticId}."));
			if (fixes.Length > 1)
			{
				// A Razor action is re-resolved through its .razor/.cshtml source, not the generated document's path.
				string actionPath = source.RazorPath ?? document.FilePath!;
				string[] candidates = fixes
					.Select(fix => $"{CodeActionService.EncodeId(actionPath, span, fix)},{fix.Kind},{diagnosticId} {fix.Action.Title}")
					.ToArray();
				return Failure(Error.Conflict(
					$"{diagnosticId} at {line}:{column} has {fixes.Length} fixes; apply the chosen candidate's actionId with apply_code_action.",
					candidates));
			}

			DiscoveredAction fix = fixes[0];
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

	/// <summary>
	/// Whether <paramref name="position"/> (0-based, in the file the caller named) lies within the diagnostic,
	/// ends included: the Razor position for a Razor file, via the generated code's #line mapping.
	/// </summary>
	private static bool Contains(RazorSourceDocument source, Location location, LinePosition position)
	{
		FileLinePositionSpan span = source.IsRazor ? location.GetMappedLineSpan() : location.GetLineSpan();
		return span.StartLinePosition <= position && position <= span.EndLinePosition;
	}
}
