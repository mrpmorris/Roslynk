using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using System.Collections.Immutable;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Documentation;
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

	[Description("""
		Fixes the diagnostic containing the supplied position, using its id and location from get_diagnostics.

		Multiple fixes return Conflict with candidate=<actionId>,Fix,<diagnosticId> <title>:
		choose by title and pass actionId to apply_code_action; do not repeat apply_code_fix.

		Errors: NotFound for an unloaded document or no diagnostic at the position; Invalid for
		line/column below 1; NotSupported for no registered fix; Conflict for no changes (without candidates).
		CS8019/IDE0005 fixes remove the @using on that line.

		Additional header: action.
		""")]
	[McpServerTool(
		Name = ApplyCodeFixName,
		Title = "Apply a diagnostic's fix",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> ApplyCodeFix(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.FilePath)] string filePath,
		[Description("Diagnostic id, e.g. CS0219/IDE0005.")] string diagnosticId,
		[Description("Diagnostic line from get_diagnostics.")] int line,
		[Description("Diagnostic column from get_diagnostics.")] int column,
		[Description(ParameterDescriptions.CheckOnly)] bool checkOnly = false,
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

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, filePath, cancellationToken);
		if (source is null)
			return Failure(Error.NotFound($"'{filePath}' is not a solution-compiled .cs, .razor or .cshtml document."));
		Document document = source.Document;

		var position = new LinePosition(line - 1, column - 1);
		string notFound = $"No {diagnosticId} diagnostic was found at {line}:{column} in '{filePath}'.";

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
