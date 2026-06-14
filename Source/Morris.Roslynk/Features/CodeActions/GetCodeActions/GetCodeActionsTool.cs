using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;

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
		Lists Roslyn's code fixes and refactorings available at a position (or selection) in a .cs file —
		each with a stable actionId to pass to apply_code_action. Fixes are driven by the compiler
		diagnostics at that span; refactorings by the span itself. Line and column are 1-based.
		""")]
	public async Task<GetCodeActionsResponse> GetCodeActions(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path of the .cs file.")] string documentPath,
		[Description("1-based line of the position.")] int line,
		[Description("1-based column of the position.")] int column,
		[Description("Optional 1-based end line for a selection.")] int? endLine = null,
		[Description("Optional 1-based end column for a selection.")] int? endColumn = null,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		Document? document = CodeActionService.FindDocument(instance.CurrentSolution, documentPath);
		if (document?.FilePath is null)
			return new GetCodeActionsResponse([], $"'{documentPath}' is not a solution-compiled .cs document.");

		SourceText text = await document.GetTextAsync(cancellationToken);
		TextSpan span = CodeActionService.SpanFor(text, line, column, endLine, endColumn);

		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);
		CodeActionDto[] dtos = actions
			.Select(action => new CodeActionDto(
				CodeActionService.EncodeId(document.FilePath, span, action),
				action.Action.Title,
				action.Kind,
				action.DiagnosticId))
			.ToArray();

		return new GetCodeActionsResponse(dtos, dtos.Length == 0 ? "No code actions are available at that position." : null);
	}
}
