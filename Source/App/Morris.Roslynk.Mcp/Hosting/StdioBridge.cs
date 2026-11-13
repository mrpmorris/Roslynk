using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The `stdio` entry point: an MCP stdio endpoint that clients launch themselves, bridging one
/// session to the persistent HTTP daemon and starting the daemon on first use. The daemon keeps the
/// warm shared workspaces the HTTP-only design exists for; stdio is only the doorbell and the pipe,
/// so a client config needs nothing beyond the launch command.
///
/// The bridge re-runs the standard handshake — probing the port and starting the daemon if needed —
/// before its next request whenever the daemon is gone, so a daemon restart alone (a plugin add or
/// remove, or a version replacement) refreshes both the connection and the tool list every client
/// sees without the client relaunching the bridge. A call in flight during the gap fails with the
/// bridge's standard failure shape; the failed request is never retried, so a write tool cannot run
/// twice off one client call.
/// </summary>
public static class StdioBridge
{
	public static async Task RunAsync()
	{
		// Same loader and object shape as the daemon, so bridge and daemon cannot disagree on the port.
		int port = RoslynkSettings.Load().EffectivePort;

		var connection = new DaemonConnection(port);
		McpClient initial = await connection.EnsureClientAsync(CancellationToken.None);

		// An empty builder registers no logging providers: nothing may write to stdout but JSON-RPC.
		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(settings: null);
		builder.Services
			.AddMcpServer(options =>
			{
				options.ServerInfo = initial.ServerInfo;
				options.ServerInstructions = initial.ServerInstructions;
			})
			.WithStdioServerTransport()
			.WithListToolsHandler(async (context, cancellationToken) =>
				await connection.CallAsync<ListToolsResult>(
					(daemon, token) => daemon.ListToolsAsync(context.Params ?? new ListToolsRequestParams(), token),
					cancellationToken))
			.WithCallToolHandler(async (context, cancellationToken) =>
				await connection.CallAsync<CallToolResult>(
					(daemon, token) => daemon.CallToolAsync(
						context.Params ?? throw new McpException("tools/call requires parameters."),
						token),
					cancellationToken));

		using IHost host = builder.Build();
		await host.RunAsync();
	}
}
