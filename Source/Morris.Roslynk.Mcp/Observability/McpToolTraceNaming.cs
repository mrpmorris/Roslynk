using ModelContextProtocol.Server;
using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Names each trace after the tool that ran. Every MCP tool call arrives over the same HTTP "POST /", so
/// the dashboard's trace list shows them all under one indistinguishable name. A CallTool filter records
/// the tool name on the request's root span as <see cref="ToolNameTag"/>; <see cref="McpToolSpanNameProcessor"/>
/// then promotes that tag to the span's display name once the HTTP instrumentation has finished naming it.
/// </summary>
public static class McpToolTraceNaming
{
	/// <summary>The tag the tool name is stashed under, also surfaced as a conventional span attribute.</summary>
	public const string ToolNameTag = "mcp.tool.name";

	/// <summary>The MCP SDK's own ActivitySource, carrying a per-call "tools/call" timing span.</summary>
	public const string McpActivitySourceName = "Experimental.ModelContextProtocol";

	public static void NameTracesAfterTools(this McpServerOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		options.Filters.Request.CallToolFilters.Add(next => (request, cancellationToken) =>
		{
			string? toolName = request.Params?.Name;
			if (toolName is not null)
			{
				Activity? root = Activity.Current;
				while (root?.Parent is not null)
					root = root.Parent;

				root?.SetTag(ToolNameTag, toolName);
			}

			return next(request, cancellationToken);
		});
	}
}
