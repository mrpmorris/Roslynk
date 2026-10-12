using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
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
	public async Task<string> GetCodeActions(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.DocumentPath)] string documentPath,
		[Description("1-based line of the position.")] int line,
		[Description("1-based column of the position.")] int column,
		[Description("Optional 1-based end line for a selection.")] int? endLine = null,
		[Description("Optional 1-based end column for a selection.")] int? endColumn = null,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(model.Solution, documentPath, cancellationToken);
		if (source?.Document.FilePath is null)
			return OutlineError.Format(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs, .razor or .cshtml document."), model.Status);

		Document document = source.Document;
		SourceText text = await source.GetSourceTextAsync(cancellationToken);
		TextSpan? mapped = await source.MapToDocumentAsync(CodeActionService.SpanFor(text, line, column, endLine, endColumn), cancellationToken);
		if (mapped is not TextSpan span)
			return OutlineError.Format(Error.NotSupported($"{line}:{column} in '{documentPath}' is not inside C# code (an @code block, expression or directive)."), model.Status);

		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);

		var builder = new OutlineBuilder();
		builder.Status(model.Status);
		builder.BeginBody();

		foreach (DiscoveredAction action in actions)
		{
			// A Razor action is re-resolved through its .razor/.cshtml source, not the generated document's path.
			string actionId = CodeActionService.EncodeId(source.RazorPath ?? document.FilePath, span, action);
			string diagnosticId = action.DiagnosticId ?? "-";
			builder.Line(0, $"{actionId},{action.Kind},{diagnosticId} {OutlineBuilder.Sanitize(action.Action.Title)}");
		}

		return builder.ToString();
	}
}
