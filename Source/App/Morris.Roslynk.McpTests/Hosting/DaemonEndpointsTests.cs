using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class DaemonEndpointsTests
{
	[Fact]
	public async Task WhenTheVersionEndpointIsRequested_ThenTheDaemonsOwnVersionIsReturned()
	{
		await using DaemonTestHost host = await DaemonTestHost.StartAsync();

		using HttpResponseMessage response = await host.Client.GetAsync(new Uri(host.BaseUri, DaemonEndpoints.VersionPath));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(DaemonVersion.Current, (await response.Content.ReadAsStringAsync()).Trim());
	}

	[Fact]
	public async Task WhenShutdownIsRequestedForTheMatchingVersion_ThenItIsRefusedAndTheHostKeepsRunning()
	{
		await using DaemonTestHost host = await DaemonTestHost.StartAsync();

		using HttpResponseMessage response = await host.Client.PostAsync(
			ShutdownUri(host, DaemonVersion.Current),
			content: null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		Assert.False(host.Stopped.IsCompleted);

		using HttpResponseMessage stillAlive = await host.Client.GetAsync(new Uri(host.BaseUri, DaemonEndpoints.VersionPath));
		Assert.Equal(HttpStatusCode.OK, stillAlive.StatusCode);
	}

	[Fact]
	public async Task WhenRestartIsRequested_ThenTheHostStops()
	{
		await using DaemonTestHost host = await DaemonTestHost.StartAsync();

		using HttpResponseMessage response = await host.Client.PostAsync(
			new Uri(host.BaseUri, DaemonEndpoints.RestartPath),
			content: null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await host.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task WhenARestartIsAlreadyInProgress_ThenASecondRestartIsRefused()
	{
		await using DaemonTestHost host = await DaemonTestHost.StartAsync();

		// Both requests are sent before the first one has stopped the host, so the process-wide
		// restart marker is the only thing deciding which one wins.
		Task<HttpResponseMessage> first = host.Client.PostAsync(new Uri(host.BaseUri, DaemonEndpoints.RestartPath), content: null);
		Task<HttpResponseMessage> second = host.Client.PostAsync(new Uri(host.BaseUri, DaemonEndpoints.RestartPath), content: null);

		using HttpResponseMessage firstResponse = await first;
		using HttpResponseMessage secondResponse = await second;

		HttpStatusCode[] statuses = [firstResponse.StatusCode, secondResponse.StatusCode];
		Assert.Contains(HttpStatusCode.Accepted, statuses);
		Assert.Contains(HttpStatusCode.Conflict, statuses);

		await host.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task WhenShutdownIsRequestedForAnotherVersion_ThenTheHostStops()
	{
		await using DaemonTestHost host = await DaemonTestHost.StartAsync();

		using HttpResponseMessage response = await host.Client.PostAsync(
			ShutdownUri(host, "0.0.1-not-this-build"),
			content: null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await host.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static Uri ShutdownUri(DaemonTestHost host, string clientVersion) =>
		new(host.BaseUri, $"{DaemonEndpoints.ShutdownPath}?{DaemonEndpoints.ClientVersionParameter}={Uri.EscapeDataString(clientVersion)}");

	private sealed class DaemonTestHost : IAsyncDisposable
	{
		private readonly WebApplication App;
		private readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

		private DaemonTestHost(WebApplication app, int port)
		{
			App = app;
			BaseUri = new Uri($"http://127.0.0.1:{port}");
			Client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(10) };
		}

		public Uri BaseUri { get; }

		public HttpClient Client { get; }

		public Task Stopped => Stopping.Task;

		public static async Task<DaemonTestHost> StartAsync()
		{
			int port = FreePort();

			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				ContentRootPath = AppContext.BaseDirectory,
				EnvironmentName = Environments.Production,
			});

			// The daemon's real logging goes to its log file; tests only need the endpoints.
			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

			WebApplication app = builder.Build();
			app.MapDaemonEndpoints();

			// The restart marker is process-wide, so a host must start from a clean slate.
			DaemonEndpoints.ResetRestartState();

			var host = new DaemonTestHost(app, port);
			app.Lifetime.ApplicationStopping.Register(() => host.Stopping.TrySetResult());

			await app.StartAsync();
			return host;
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await App.DisposeAsync();
		}

		private static int FreePort()
		{
			using var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			return ((IPEndPoint)probe.LocalEndpoint).Port;
		}
	}
}
