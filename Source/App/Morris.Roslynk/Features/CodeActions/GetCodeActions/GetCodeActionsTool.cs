using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

[McpServerToolType]
public sealed class GetCodeActionsTool : IMcpTool
{
	public const string GetCodeActionsName = "get_code_actions";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly CodeActionService CodeActionService;

	public GetCodeActionsTool(InstanceRegistry instanceRegistry, CodeActionService codeActionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
	}

	public static string GetName() => GetCodeActionsName;

	public static string GetDescription() => """
		Lists fixes/refactorings at a source position or selection. Fixes use compiler/analyzer
		diagnostics at the span; refactorings use the span itself.

		Returns up to 50 <actionId>,<kind>,<diagnosticId> <title> lines; diagnosticId='-' for refactorings.
		filePath accepts absolute, solution-relative, or a unique path suffix/file name; a fallback match
		adds resolvedPath=<solution-relative path>. Unknown filePath returns NotFound; several matching
		files return Ambiguous with those paths as candidates.
		Razor offers compiler fixes/refactorings; positions outside C# return NotSupported.
		""";

	[McpServerTool(
		Name = GetCodeActionsName,
		Title = "List code actions",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetCodeActions(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.FilePath)] string filePath,
		[Description("Position line.")] int line,
		[Description("Position column.")] int column,
		[Description("Selection end line.")] int? endLine = null,
		[Description("Selection end column.")] int? endColumn = null,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, token) =>
				GetCodeActionsCoreAsync(
					model: model,
					instance: instance,
					filePath: filePath,
					line: line,
					column: column,
					endLine: endLine,
					endColumn: endColumn,
					cancellationToken: token
				),
			cancellationToken);
	}

	/// <summary>
	/// The pinned-snapshot core: resolves the position and discovers actions against the captured model
	/// only, so a publication mid-flight cannot mix two generations into one action list.
	/// </summary>
	internal async Task<string> GetCodeActionsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string filePath,
		int line,
		int column,
		int? endLine,
		int? endColumn,
		CancellationToken cancellationToken = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

		(RazorSourceDocument? source, Error? pathError) = await RazorSourceDocument.ResolveWithErrorAsync(
			model.Solution,
			filePath,
			$"'{filePath}' is not a solution-compiled .cs, .razor or .cshtml document.",
			cancellationToken);
		if (pathError is Error error)
			return OutlineError.Format(error, model.Status);
		if (source?.Document.FilePath is null)
			return OutlineError.Format(Error.NotFound($"'{filePath}' is not a solution-compiled .cs, .razor or .cshtml document."), model.Status);

		Document document = source.Document;
		SourceText text = await source.GetSourceTextAsync(cancellationToken);
		TextSpan? mapped = await source.MapToDocumentAsync(CodeActionService.SpanFor(text, line, column, endLine, endColumn), cancellationToken);
		if (mapped is not TextSpan span)
			return OutlineError.Format(Error.NotSupported($"{line}:{column} in '{filePath}' is not inside C# code (an @code block, expression or directive)."), model.Status);

		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);

		var builder = new OutlineBuilder();
		builder.Status(model.Status);
		if (source.ResolvedBySuffix)
			builder.Header("resolvedPath", source.ResolvedPath);
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
