using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// Wraps every Roslynk tool so three failure modes that bypass a tool's own error handling are covered:
/// the published schema is rewritten so defaulted parameters are genuinely omittable, an argument the
/// SDK cannot bind - a wrong type, an unparseable value - comes back as the standard header-only
/// 'error='/'errorMessage=' result instead of an unhandled exception, and a result larger than the
/// server-wide <see cref="ResponseBudget"/> is cut after its last whole line and marked (the backstop
/// for the tools that cannot page - get_symbol, find_definition, get_expression_info,
/// get_solution_status). multi_query is exempt: it enforces the budget itself with whole-or-drop
/// selection, and a cut here would hide an oversized result from that arithmetic.
/// </summary>
/// <remarks>
/// A C# optional parameter is emitted by the MCP SDK as a property that is absent from "required" but
/// carries a JSON Schema "default" annotation. Several clients convert the schema to their own validator
/// and treat a property that has a "default" as one that must be present, so omitting it is rejected
/// before the call reaches the server ("expected nonoptional, received undefined"). Moving the keyword
/// into the description keeps the caller informed of the default while leaving nothing for a client to
/// mistranslate, and the text is generated from the real C# default so it cannot drift from the
/// signature.
/// </remarks>
internal sealed class RoslynkTool : DelegatingMcpServerTool
{
	/// <summary>The one tool whose own budget enforcement replaces the cut backstop.</summary>
	private const string MultiQueryName = "multi_query";

	private static readonly AIJsonSchemaTransformOptions SchemaTransformOptions =
		new() { MoveDefaultKeywordToDescription = true };

	private readonly Tool Published;
	private readonly ResponseBudget Budget;

	public RoslynkTool(McpServerTool innerTool, ResponseBudget? budget = null)
		: base(innerTool)
	{
		Published = Republish(innerTool.ProtocolTool);
		Budget = budget ?? ResponseBudget.Default;
	}

	public override Tool ProtocolTool => Published;

	public override async ValueTask<CallToolResult> InvokeAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			CallToolResult result = await base.InvokeAsync(request, cancellationToken);

			// Last line of defence for the tools that cannot page (get_symbol, find_definition,
			// get_expression_info, get_solution_status): a result over the budget is cut after its last
			// whole line and marked. multi_query is exempt - it enforces the budget itself with
			// whole-or-drop selection, and a cut here would hide an oversized result from that arithmetic;
			// every paging tool pages itself under the budget before this point.
			if (Published.Name != MultiQueryName
				&& result.Content is [TextContentBlock { } text, ..] && text.Text.Length > Budget.MaxChars)
				result = new CallToolResult
				{
					Content = [new TextContentBlock { Text = ResponseBudget.Fit(text.Text, Budget.MaxChars) }],
					IsError = result.IsError
				};

			return result;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			return ToErrorResult(exception);
		}
	}

	/// <summary>
	/// Copies the protocol tool through JSON so every field the SDK filled in is preserved, replacing only
	/// the input schema. The original instance is left untouched because the SDK caches and shares it.
	/// An empty description is normalized to null: the SDK pipeline (AIFunctionFactory) yields an empty
	/// string when a tool method has no [Description], and Roslynk publishes no descriptions at all - tool
	/// guidance lives in the roslynk skill - so the empty placeholder must not reach the wire.
	/// </summary>
	private static Tool Republish(Tool tool)
	{
		string json = JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions);
		Tool copy = JsonSerializer.Deserialize<Tool>(json, McpJsonUtilities.DefaultOptions)
			?? throw new InvalidOperationException($"Could not copy the protocol definition of tool '{tool.Name}'.");
		copy.InputSchema = AIJsonUtilities.TransformSchema(tool.InputSchema, SchemaTransformOptions);
		copy.Description = string.IsNullOrEmpty(copy.Description) ? null : copy.Description;
		return copy;
	}

	/// <summary>
	/// Maps an exception that escaped a tool onto the standard header-only failure result.
	/// </summary>
	public static CallToolResult ToErrorResult(Exception exception)
	{
		// A binding failure is the caller's fault and is reported as Invalid; anything else is a fault in
		// the tool itself. Both are shaped like every other Roslynk failure so a caller never has to parse
		// a transport-level exception.
		Error error = ToError(exception);
		return new CallToolResult
		{
			IsError = true,
			Content = [new TextContentBlock { Text = OutlineError.Format(error, SolutionStatus.Ready) }]
		};
	}

	/// <summary>
	/// The shared exception-to-<see cref="Error"/> rule: a binding failure (bad argument type, unparseable
	/// value) is the caller's fault and is Invalid; anything else is a fault in the tool itself and is
	/// Faulted. An <see cref="OperationCanceledException"/> is deliberately not mapped - callers rethrow it,
	/// matching every tool's cancellation behavior. multi_query reuses this per operation so a slot's error
	/// block agrees exactly with what the standalone path would have returned.
	/// </summary>
	public static Error ToError(Exception exception) =>
		exception is ArgumentException or FormatException or JsonException or NotSupportedException
			? Error.Invalid(exception.Message)
			: Error.Faulted(exception.Message);
}
