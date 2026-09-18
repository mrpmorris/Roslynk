using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// Wraps every Roslynk tool so the two failure modes that bypass a tool's own error handling are covered:
/// the published schema is rewritten by <see cref="ToolSchemaCompatibility"/> so defaulted parameters are
/// genuinely omittable, and an argument the SDK cannot bind - a wrong type, an unparseable value - comes
/// back as the standard header-only 'error='/'errorMessage=' result instead of an unhandled exception.
/// </summary>
public sealed class RoslynkTool : DelegatingMcpServerTool
{
	private readonly Tool Published;

	public RoslynkTool(McpServerTool innerTool)
		: base(innerTool)
	{
		Published = Republish(innerTool.ProtocolTool);
	}

	public override Tool ProtocolTool => Published;

	public override async ValueTask<CallToolResult> InvokeAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			return await base.InvokeAsync(request, cancellationToken);
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
	/// </summary>
	private static Tool Republish(Tool tool)
	{
		string json = JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions);
		Tool copy = JsonSerializer.Deserialize<Tool>(json, McpJsonUtilities.DefaultOptions)
			?? throw new InvalidOperationException($"Could not copy the protocol definition of tool '{tool.Name}'.");
		copy.InputSchema = ToolSchemaCompatibility.Rewrite(tool.InputSchema);
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
		Error error = exception is ArgumentException or FormatException or JsonException or NotSupportedException
			? Error.Invalid(exception.Message)
			: Error.Faulted(exception.Message);

		return new CallToolResult
		{
			IsError = true,
			Content = [new TextContentBlock { Text = OutlineError.Format(error, SolutionStatus.Ready) }]
		};
	}
}
