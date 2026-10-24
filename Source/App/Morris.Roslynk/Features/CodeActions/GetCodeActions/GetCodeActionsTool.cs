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

	[Description("""
		Lists fixes/refactorings at a source position or selection. Fixes use compiler/analyzer
		diagnostics at the span; refactorings use the span itself.

		Returns up to 50 <actionId>,<kind>,<diagnosticId> <title> lines; diagnosticId='-' for refactorings.
		filePath accepts absolute, solution-relative, or a unique path suffix/file name; a fallback match
		adds resolvedPath=<solution-relative path>. Unknown filePath returns NotFound; several matching
		files return Ambiguous with those paths as candidates.
		Razor offers compiler fixes/refactorings; positions outside C# return NotSupported.
		""")]
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
					Model: model,
					Instance: instance,
					FilePath: filePath,
					Line: line,
					Column: column,
					EndLine: endLine,
					EndColumn: endColumn,
					CancellationToken: token
				),
			cancellationToken);
	}

	/// <summary>
	/// The pinned-snapshot core: resolves the position and discovers actions against the captured model
	/// only, so a publication mid-flight cannot mix two generations into one action list.
	/// </summary>
	internal async Task<string> GetCodeActionsCoreAsync(
		SolutionModel Model,
		RoslynInstance Instance,
		string FilePath,
		int Line,
		int Column,
		int? EndLine,
		int? EndColumn,
		CancellationToken CancellationToken = default)
	{
		if (Model.Solution is null)
			return OutlineError.Format(Error.Indexing(Instance.LoadedProjects), Model.Status);

		(RazorSourceDocument? source, Error? pathError) = await RazorSourceDocument.ResolveWithErrorAsync(
			Model.Solution,
			FilePath,
			$"'{FilePath}' is not a solution-compiled .cs, .razor or .cshtml document.",
			CancellationToken);
		if (pathError is Error error)
			return OutlineError.Format(error, Model.Status);
		if (source?.Document.FilePath is null)
			return OutlineError.Format(Error.NotFound($"'{FilePath}' is not a solution-compiled .cs, .razor or .cshtml document."), Model.Status);

		Document document = source.Document;
		SourceText text = await source.GetSourceTextAsync(CancellationToken);
		TextSpan? mapped = await source.MapToDocumentAsync(CodeActionService.SpanFor(text, Line, Column, EndLine, EndColumn), CancellationToken);
		if (mapped is not TextSpan span)
			return OutlineError.Format(Error.NotSupported($"{Line}:{Column} in '{FilePath}' is not inside C# code (an @code block, expression or directive)."), Model.Status);

		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, CancellationToken);

		var builder = new OutlineBuilder();
		builder.Status(Model.Status);
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
