using OpenTelemetry;
using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Promotes the <see cref="McpToolTraceNaming.ToolNameTag"/> recorded on an HTTP request span to that
/// span's display name. Runs at span end, after the ASP.NET Core instrumentation has applied its own
/// "{method} {route}" name, so the tool name is the final word the exporter sees. Only ASP.NET request
/// spans are renamed: the CallTool filter tags every span of the call's ancestry for the exporter's
/// noise filter, and the MCP SDK's own "tools/call" span keeps that name to stay distinguishable.
/// </summary>
public sealed class McpToolSpanNameProcessor : BaseProcessor<Activity>
{
	public override void OnEnd(Activity activity)
	{
		ArgumentNullException.ThrowIfNull(activity);

		if (activity.Source.Name == McpToolTraceNaming.AspNetCoreActivitySourceName &&
			activity.GetTagItem(McpToolTraceNaming.ToolNameTag) is string toolName && toolName.Length > 0)
			activity.DisplayName = toolName;
	}
}
