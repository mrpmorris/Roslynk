using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk;
using Morris.Roslynk.Mcp;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

/// <summary>
/// The stdio bridge's recovery behavior: when the daemon behind the bridge is replaced (a plugin add
/// or remove restarts it), the next bridged request re-runs the standard handshake against the new
/// daemon and succeeds, while the call that was in flight during the gap failed with the bridge's
/// standard failure shape and was not retried.
/// </summary>
public class StdioBridgeReconnectTests
{
	[Fact]
	public async Task WhenTheDaemonIsReplacedBetweenRequests_ThenTheNextBridgedRequestReconnectsAndAnswers()
	{
		int port = FreePort();
		BridgeTestHost host = await BridgeTestHost.StartAsync(port, serverTitle: "first daemon");
		var connection = new DaemonConnection(port);

		ListToolsResult first = await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None);
		Assert.Equal(["ping"], first.Tools.Select(tool => tool.Name).ToArray());

		// The daemon replacement: the old host stops, releasing the port, and a new daemon answers.
		await host.DisposeAsync();

		await Assert.ThrowsAnyAsync<Exception>(async () => await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None));

		await using BridgeTestHost replacement = await BridgeTestHost.StartAsync(port, serverTitle: "second daemon");

		// The next request re-runs the standard handshake — the probe finds the new daemon listening
		// and the bridge connects to it, so no client relaunch is needed, and the tool list the bridge
		// serves is the new daemon's.
		ListToolsResult second = await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None);
		Assert.Equal(["pong"], second.Tools.Select(tool => tool.Name).ToArray());
	}

	[Fact]
	public async Task WhenTheDaemonIsGoneAndNothingReplacesIt_ThenTheNextRequestStillFails()
	{
		int port = FreePort();
		BridgeTestHost host = await BridgeTestHost.StartAsync(port, serverTitle: "first daemon");
		var connection = new DaemonConnection(port);

		ListToolsResult first = await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None);
		Assert.NotEmpty(first.Tools);

		await host.DisposeAsync();

		// Nothing re-answers the port: the handshake probes it, cannot start a daemon of its own from
		// a test, and the request fails rather than hanging.
		await Assert.ThrowsAnyAsync<Exception>(async () => await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None));
		await Assert.ThrowsAnyAsync<Exception>(async () => await connection.CallAsync<ListToolsResult>(
			(daemon, token) => daemon.ListToolsAsync(new ListToolsRequestParams(), token), CancellationToken.None));
	}

	private static int FreePort()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	/// <summary>A minimal daemon stand-in: loopback HTTP, MCP mapped, a single tool whose title names
	/// the host instance, so a reconnection to the replacement is observable in the response.</summary>
	private sealed class BridgeTestHost : IAsyncDisposable
	{
		private readonly WebApplication App;

		private BridgeTestHost(WebApplication app)
		{
			App = app;
		}

		public static async Task<BridgeTestHost> StartAsync(int port, string serverTitle)
		{
			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				ContentRootPath = AppContext.BaseDirectory,
				EnvironmentName = Environments.Production,
			});

			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

			// Each stand-in daemon exposes a differently named tool, so a response names which daemon
			// answered and a reconnection to the replacement is observable.
			MethodInfo toolMethod = serverTitle == "first daemon" ? PingMethod : PongMethod;

			builder.Services
				.AddMcpServer(options => options.ServerInfo = new Implementation
				{
					Name = "Roslynk",
					Title = serverTitle,
					Version = "0.0.0-test",
				})
				.WithHttpTransport()
				.WithTools([McpServerTool.Create(toolMethod)]);

			WebApplication app = builder.Build();
			app.MapMcp();
			// The handshake probes /version before reusing a daemon; the stand-in answers it the way
			// the real daemon does.
			app.MapDaemonEndpoints();
			await app.StartAsync();
			return new BridgeTestHost(app);
		}

		private static readonly MethodInfo PingMethod = typeof(BridgeTestHost)
			.GetMethod(nameof(Ping), BindingFlags.Public | BindingFlags.Static)!;

		private static readonly MethodInfo PongMethod = typeof(BridgeTestHost)
			.GetMethod(nameof(Pong), BindingFlags.Public | BindingFlags.Static)!;

		[McpServerTool]
		public static string Ping() => "pong";

		[McpServerTool]
		public static string Pong() => "ping";

		public async ValueTask DisposeAsync() => await App.DisposeAsync();
	}
}
