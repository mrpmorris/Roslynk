using OpenTelemetry;
using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Promotes the <see cref="McpToolTraceNaming.ToolNameTag"/> recorded on a request's root span to that
/// span's display name. Runs at span end, after the ASP.NET Core instrumentation has applied its own
/// "{method} {route}" name, so the tool name is the final word the exporter sees.
/// </summary>
public sealed class McpToolSpanNameProcessor : BaseProcessor<Activity>
{
	public override void OnEnd(Activity activity)
	{
		ArgumentNullException.ThrowIfNull(activity);

		if (activity.GetTagItem(McpToolTraceNaming.ToolNameTag) is string toolName && toolName.Length > 0)
			activity.DisplayName = toolName;
	}
}
