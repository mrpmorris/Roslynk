using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Observability;
using System.Diagnostics;
using System.Text.Json;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Names each trace after the tool that ran, records the call's arguments on it, and tags it with the MCP
/// session so the dashboard can group a session's calls together. Every MCP tool call arrives over the
/// same HTTP "POST /", so the trace list would otherwise show them all under one indistinguishable name.
/// A CallTool filter tags every span of the call's ancestry with the tool name — the exporter's noise
/// filter keeps a request span only when it carries that tag — and records the call's arguments on the
/// outermost span; <see cref="McpToolSpanNameProcessor"/> then promotes the tool name to the span's
/// display name once the HTTP instrumentation has finished naming it.
/// </summary>
public static class McpToolTraceNaming
{
	/// <summary>The tag the tool name is stashed under, also surfaced as a conventional span attribute.</summary>
	public const string ToolNameTag = "mcp.tool.name";

	/// <summary>The prefix for each recorded argument; the argument's name is appended.</summary>
	public const string ArgumentTagPrefix = "mcp.tool.arg.";

	/// <summary>The tag the MCP session id is recorded under, for grouping a session's calls.</summary>
	public const string SessionIdTag = "mcp.session.id";

	/// <summary>The Streamable HTTP header carrying the per-connection MCP session id.</summary>
	public const string SessionIdHeader = "Mcp-Session-Id";

	/// <summary>The MCP SDK's own ActivitySource, carrying a per-call "tools/call" timing span.</summary>
	public const string McpActivitySourceName = "Experimental.ModelContextProtocol";

	/// <summary>The ASP.NET Core ActivitySource every HTTP request span is emitted from.</summary>
	public const string AspNetCoreActivitySourceName = "Microsoft.AspNetCore";

	public static void NameTracesAfterTools(this McpServerOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		options.Filters.Request.CallToolFilters.Add(next => (request, cancellationToken) =>
		{
			CallToolRequestParams? parameters = request.Params;
			if (parameters is not null)
			{
				Activity? outermost = null;
				for (Activity? span = Activity.Current; span is not null; span = span.Parent)
				{
					span.SetTag(ToolNameTag, parameters.Name);
					outermost = span;
				}

				if (outermost is not null)
					Record(outermost, parameters);
			}

			return next(request, cancellationToken);
		});
	}

	/// <summary>Records the MCP session id (if the request carried one) onto the request's root span.</summary>
	public static void RecordSessionId(Activity activity, HttpRequest request)
	{
		string? sessionId = request.Headers[SessionIdHeader];
		if (!string.IsNullOrEmpty(sessionId))
			activity.SetTag(SessionIdTag, sessionId);
	}

	private static void Record(Activity root, CallToolRequestParams parameters)
	{
		root.SetTag(ToolNameTag, parameters.Name);

		if (parameters.Arguments is null)
			return;

		foreach (KeyValuePair<string, JsonElement> argument in parameters.Arguments)
			root.SetTag(ArgumentTagPrefix + argument.Key, Render(argument.Value));
	}

	private static string Render(JsonElement value) =>
		value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: value.GetRawText();
}
