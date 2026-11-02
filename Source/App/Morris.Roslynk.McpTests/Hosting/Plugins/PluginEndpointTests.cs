using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting.Plugins;

public class PluginEndpointTests
{
	private const string EchoPlugin = """
		using System;
		using System.ComponentModel;
		using System.Threading;
		using System.Threading.Tasks;
		using ModelContextProtocol.Server;
		using Morris.Roslynk.Plugins;

		namespace Echo;

		public sealed class EchoPlugin : IRoslynkPlugin
		{
			public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
			public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public async Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping)
			{
				Started.TrySetResult(true);
				try { await Task.Delay(Timeout.Infinite, stopping); }
				finally { Stopped.TrySetResult(true); }
			}
		}

		public sealed class FailingPlugin : IRoslynkPlugin
		{
			public Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping) =>
				throw new InvalidOperationException("This plugin fails at once.");
		}

		[McpServerToolType]
		public sealed class EchoTools
		{
			private readonly EchoPlugin Plugin;
			private readonly ISolutionProvider SolutionProvider;

			public EchoTools(EchoPlugin plugin, ISolutionProvider solutionProvider)
			{
				Plugin = plugin;
				SolutionProvider = solutionProvider;
			}

			[McpServerTool(Name = "echo_model"), Description("Reports the plugin's state and a solution's model status.")]
			public async Task<string> EchoModel(string solutionPath, CancellationToken cancellationToken)
			{
				GetModelResponse response = await SolutionProvider.GetModelAsync(solutionPath, cancellationToken);
				return $"started={Plugin.Started.Task.IsCompleted} status={response.Status} error={response.ErrorMessage}";
			}
		}
		""";

	[Fact]
	public async Task WhenAClientConnectsToAPluginsEndpoint_ThenItSeesOnlyThePluginsTools()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await using McpClient client = await host.ConnectAsync("Echo");

		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Equal("echo_model", Assert.Single(tools).Name);
		Assert.Equal("Roslynk plugin Echo", client.ServerInfo.Name);
	}

	[Fact]
	public async Task WhenAPluginsToolIsCalled_ThenItReadsModelsThroughTheProviderAndSeesItsPluginInstance()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await using McpClient client = await host.ConnectAsync("Echo");
		string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.slnx");

		CallToolResult result = await client.CallToolAsync("echo_model", new Dictionary<string, object?> { ["solutionPath"] = missing });

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.StartsWith("started=True status=Faulted error=No solution file", text);
	}

	[Fact]
	public async Task WhenAClientConnectsToRoslynksOwnEndpoint_ThenItSeesRoslynksToolsAndNoPlugins()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await using McpClient pluginClient = await host.ConnectAsync("Echo");
		await using McpClient client = await host.ConnectAsync();

		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Contains(tools, tool => tool.Name == "open_solution");
		Assert.DoesNotContain(tools, tool => tool.Name == "echo_model");
		Assert.Equal("Roslynk", client.ServerInfo.Name);
	}

	[Fact]
	public async Task WhenNoPluginHasTheName_ThenItsEndpointIsNotFound()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));

		using var request = new HttpRequestMessage(HttpMethod.Post, PluginEndpoints.PathFor("Missing"))
		{
			Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
		};
		request.Headers.Accept.ParseAdd("application/json, text/event-stream");
		using HttpResponseMessage response = await host.Client.SendAsync(request);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task WhenOnePluginFails_ThenTheOthersKeepRunningUntilTheDaemonStops()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		object echo = host.Instance("EchoPlugin");

		await TaskOf(echo, "Started").WaitAsync(TimeSpan.FromSeconds(10));
		Assert.False(TaskOf(echo, "Stopped").IsCompleted);

		await host.StopAsync();

		await TaskOf(echo, "Stopped").WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static Task TaskOf(object plugin, string property)
	{
		object source = plugin.GetType().GetProperty(property)!.GetValue(plugin)!;
		return (Task)source.GetType().GetProperty("Task")!.GetValue(source)!;
	}
}
