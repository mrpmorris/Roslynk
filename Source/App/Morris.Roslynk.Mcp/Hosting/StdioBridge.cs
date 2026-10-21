using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The `stdio` entry point: an MCP stdio endpoint that clients launch themselves, bridging one
/// session to the persistent HTTP daemon and starting the daemon on first use. The daemon keeps the
/// warm shared workspaces the HTTP-only design exists for; stdio is only the doorbell and the pipe,
/// so a client config needs nothing beyond the launch command.
/// </summary>
public static class StdioBridge
{
	public static async Task RunAsync()
	{
		// Same loader and object shape as the daemon, so bridge and daemon cannot disagree on the port.
		int port = RoslynkSettings.Load().EffectivePort;

		await DaemonLauncher.EnsureRunningAsync(port, CancellationToken.None);

		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri($"http://localhost:{port}"),
			TransportMode = HttpTransportMode.StreamableHttp,
			Name = "Roslynk daemon",
		});

		// Disposing the client on the way out sends the session DELETE, so the daemon releases this
		// session's solution ref-counts and idle eviction can reclaim the workspaces.
		await using McpClient daemon = await McpClient.CreateAsync(transport);

		// An empty builder registers no logging providers: nothing may write to stdout but JSON-RPC.
		HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(settings: null);
		builder.Services
			.AddMcpServer(options =>
			{
				options.ServerInfo = daemon.ServerInfo;
				options.ServerInstructions = daemon.ServerInstructions;
			})
			.WithStdioServerTransport()
			.WithListToolsHandler(async (context, cancellationToken) =>
				await daemon.ListToolsAsync(context.Params ?? new ListToolsRequestParams(), cancellationToken))
			.WithCallToolHandler(async (context, cancellationToken) =>
				await daemon.CallToolAsync(
					context.Params ?? throw new McpException("tools/call requires parameters."),
					cancellationToken));

		using IHost host = builder.Build();
		await host.RunAsync();
	}
}
