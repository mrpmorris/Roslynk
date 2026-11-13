using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morris.Roslynk;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Mcp;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Plugins;

/// <summary>
/// The plugin feature end to end over the real host composition: a staged plugins root, the real
/// discovery, loader, tool registration and event pump, MCP clients over HTTP, and the /plugins
/// endpoint. The sample plugin deploys from its built output, exactly like a `plugin add` from build
/// output would.
/// </summary>
public class PluginEndToEndTests : IDisposable
{
	private readonly string Root;

	public PluginEndToEndTests()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslynk-plugin-e2e-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Root);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A loaded plugin's non-collectible load context keeps its DLL mapped until process exit.
		}
	}

	[Fact]
	public async Task WhenAPluginIsDeployedAndTheDaemonStarts_ThenItsToolsAreListedPrefixedAndCallable()
	{
		DeploySamplePlugin();
		await using PluginTestHost host = await PluginTestHost.StartAsync(Root);
		await using McpClient client = await host.CreateClientAsync();

		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Contains(tools, tool => tool.Name == "sampleplugin__CountProjects");
		Assert.Contains(tools, tool => tool.Name == "sampleplugin__CountDiagnostics");
	}

	[Fact]
	public async Task WhenAPluginToolIsCalled_ThenItRoundTripsThroughTheInvocationWrapperToThePluginInstance()
	{
		DeploySamplePlugin();
		await using PluginTestHost host = await PluginTestHost.StartAsync(Root);
		await using McpClient client = await host.CreateClientAsync();

		CallToolResult missing = await client.CallToolAsync(
			"sampleplugin__CountProjects",
			new Dictionary<string, object?> { ["solutionId"] = "C:/nowhere/missing.slnx" });

		string text = Assert.IsType<TextContentBlock>(missing.Content.Single()).Text;
		Assert.Equal("error=NotFound", text);

		CallToolResult loaded = await client.CallToolAsync(
			"sampleplugin__CountProjects",
			new Dictionary<string, object?> { ["solutionId"] = SimpleSolutionPath() });

		string projects = Assert.IsType<TextContentBlock>(loaded.Content.Single()).Text;
		Assert.Matches(@"^projects=\d+$", projects);
	}

	[Fact]
	public async Task WhenThePluginsEndpointIsRequested_ThenItReportsLoadedPluginsAndTheirToolNames()
	{
		DeploySamplePlugin();
		await using PluginTestHost host = await PluginTestHost.StartAsync(Root);

		using HttpResponseMessage response = await host.Client.GetAsync(new Uri(host.BaseUri, DaemonEndpoints.PluginsPath));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		JsonElement payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
		JsonElement plugin = Assert.Single(payload.EnumerateArray(), item => item.GetProperty("name").GetString() == "SamplePlugin");
		string[] toolNames = plugin.GetProperty("tools").EnumerateArray().Select(item => item.GetString()!).ToArray();
		Assert.Equal(["sampleplugin__CountProjects", "sampleplugin__CountDiagnostics"], toolNames);
	}

	[Fact]
	public async Task WhenABrokenDeploymentSitsBesideAValidOne_ThenTheDaemonStillStartsAndOnlyTheValidPluginLoads()
	{
		DeploySamplePlugin();
		// A folder whose entry DLL base name does not match the folder name: reported and skipped.
		string broken = Path.Combine(Root, "BrokenPlugin");
		Directory.CreateDirectory(broken);
		File.Copy(SamplePluginEntryPath(), Path.Combine(broken, "WrongName.Roslynk.Plugin.dll"));

		await using PluginTestHost host = await PluginTestHost.StartAsync(Root);
		await using McpClient client = await host.CreateClientAsync();

		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Contains(tools, tool => tool.Name == "sampleplugin__CountProjects");
		Assert.DoesNotContain(tools, tool => tool.Name!.Contains("brokenplugin", StringComparison.OrdinalIgnoreCase));
		JsonElement payload = JsonDocument.Parse(
			await host.Client.GetStringAsync(new Uri(host.BaseUri, DaemonEndpoints.PluginsPath))).RootElement;
		Assert.Single(payload.EnumerateArray(), item => item.GetProperty("name").GetString() == "SamplePlugin");
	}

	private void DeploySamplePlugin()
	{
		string folder = Path.Combine(Root, "SamplePlugin");
		Directory.CreateDirectory(folder);
		string binDirectory = Path.GetDirectoryName(typeof(SamplePlugin.Roslynk.Plugin.PluginFactory).Assembly.Location)!;
		foreach (string fileName in new[] { "SamplePlugin.Roslynk.Plugin.dll", "SamplePlugin.Roslynk.Plugin.deps.json", "Morris.Roslynk.Plugins.Abstractions.dll" })
		{
			string source = Path.Combine(binDirectory, fileName);
			Assert.True(File.Exists(source), $"The sample plugin's '{fileName}' is not in the test output.");
			File.Copy(source, Path.Combine(folder, fileName));
		}
	}

	private static string SamplePluginEntryPath()
	{
		string binDirectory = Path.GetDirectoryName(typeof(SamplePlugin.Roslynk.Plugin.PluginFactory).Assembly.Location)!;
		return Path.Combine(binDirectory, "SamplePlugin.Roslynk.Plugin.dll");
	}

	private static string SimpleSolutionPath()
	{
		// The committed Simple fixture loads without NuGet assets, so a plugin tool call can run a
		// real solution load without a restore.
		string? current = AppContext.BaseDirectory;
		while (current is not null && !File.Exists(Path.Combine(current, "Source", "Morris.Roslynk.slnx")))
			current = Directory.GetParent(current)?.FullName;
		Assert.NotNull(current);
		string path = Path.Combine(current!, "Source", "TestFixtures", "SimpleSolution", "SimpleSolution.slnx");
		Assert.True(File.Exists(path), "The Simple fixture solution was not found relative to the test output.");
		return path;
	}

	private sealed class PluginTestHost : IAsyncDisposable
	{
		private readonly WebApplication App;

		private PluginTestHost(WebApplication app, int port)
		{
			App = app;
			BaseUri = new Uri($"http://127.0.0.1:{port}");
			Client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(60) };
		}

		public Uri BaseUri { get; }

		public HttpClient Client { get; }

		public static async Task<PluginTestHost> StartAsync(string pluginsRoot)
		{
			int port = FreePort();

			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				ContentRootPath = AppContext.BaseDirectory,
				EnvironmentName = Environments.Production,
			});

			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

			RoslynkSettings settings = new();
			builder.Services.AddRoslynk(
				new ResponseBudget(settings.EffectiveMaxChars),
				settings.EffectiveLoadWaitTimeout,
				settings.EffectivePluginStartupTimeout,
				pluginsRoot);
			builder.Services.AddSingleton(settings);

			builder.Services
				.AddMcpServer(McpServerRegistration.Configure)
				.WithHttpTransport()
				.WithRoslynkTools();

			WebApplication app = builder.Build();
			app.UseMcpBodyValidation();
			app.MapMcp();
			app.MapDaemonEndpoints();

			var host = new PluginTestHost(app, port);
			await app.StartAsync();
			return host;
		}

		public async Task<McpClient> CreateClientAsync()
		{
			var transport = new ModelContextProtocol.Client.HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = BaseUri,
				TransportMode = HttpTransportMode.StreamableHttp,
				Name = "plugin e2e test",
			});
			return await McpClient.CreateAsync(transport);
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
