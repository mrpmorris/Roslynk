using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

[McpServerToolType]
public sealed class GetCodeActionsTool
{
	public const string GetCodeActionsName = "get_code_actions";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly CodeActionService CodeActionService;

	public GetCodeActionsTool(InstanceRegistry instanceRegistry, CodeActionService codeActionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
	}

	[McpServerTool(
		Name = GetCodeActionsName,
		Title = "List code actions",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Lists Roslyn's code fixes and refactorings available at a position (or selection) in a .cs file.
		Returns a text result, not JSON: a blank line, then one
		'<actionId>,<kind>,<diagnosticId> <title>' line per action (diagnosticId is '-' for a refactoring; the
		title is free text and trails last). The actionId is opaque and must be passed back verbatim to
		apply_code_action. Fixes are driven by the compiler diagnostics at that span; refactorings by the span
		itself. Line and column are 1-based. Prefer discovering a fix here over editing by hand.
		""")]
	public async Task<string> GetCodeActions(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path of the .cs file; absolute, or relative to the solution folder.")] string documentPath,
		[Description("1-based line of the position.")] int line,
		[Description("1-based column of the position.")] int column,
		[Description("Optional 1-based end line for a selection.")] int? endLine = null,
		[Description("Optional 1-based end column for a selection.")] int? endColumn = null,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		Document? document = CodeActionService.FindDocument(model.Solution, documentPath);
		if (document?.FilePath is null)
			return OutlineError.Format(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs document."), model.Status);

		SourceText text = await document.GetTextAsync(cancellationToken);
		TextSpan span = CodeActionService.SpanFor(text, line, column, endLine, endColumn);

		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);

		var builder = new OutlineBuilder();
		builder.Status(model.Status);
		builder.BeginBody();

		foreach (DiscoveredAction action in actions)
		{
			string actionId = CodeActionService.EncodeId(document.FilePath, span, action);
			string diagnosticId = action.DiagnosticId ?? "-";
			builder.Line(0, $"{actionId},{action.Kind},{diagnosticId} {OutlineBuilder.Sanitize(action.Action.Title)}");
		}

		return builder.ToString();
	}
}
