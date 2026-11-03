using System.Diagnostics;
using System.Net;
using System.Text;
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
			public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping)
			{
				Started.TrySetResult(true);
				throw new InvalidOperationException("This plugin fails at once.");
			}
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

			[McpServerTool(Name = "explode"), Description("Always throws.")]
			public string Explode() => throw new InvalidOperationException("The tool's own failure.");
		}
		""";

	private const string DuplicateToolsPlugin = """
		using ModelContextProtocol.Server;

		namespace Duplicate;

		[McpServerToolType]
		public sealed class DuplicateTools
		{
			[McpServerTool(Name = "lookup")] public static string ByNumber(int number) => "";
			[McpServerTool(Name = "lookup")] public static string ByName(string name) => "";
		}
		""";

	private const string StubbornPlugin = """
		using System.Threading;
		using System.Threading.Tasks;
		using Morris.Roslynk.Plugins;

		namespace Stubborn;

		public sealed class StubbornPlugin : IRoslynkPlugin
		{
			public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public async Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping)
			{
				Started.TrySetResult(true);
				await Task.Delay(Timeout.Infinite);
			}
		}
		""";

	[Fact]
	public async Task WhenAClientConnectsToAPluginsEndpoint_ThenItSeesOnlyThePluginsTools()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await using McpClient client = await host.ConnectAsync("Echo");

		IList<McpClientTool> tools = await client.ListToolsAsync();

		Assert.Equal(["echo_model", "explode"], tools.Select(tool => tool.Name).Order());
		Assert.Equal("Roslynk plugin Echo", client.ServerInfo.Name);
	}

	[Fact]
	public async Task WhenAPluginsToolIsCalled_ThenItReadsModelsThroughTheProviderAndSeesItsPluginInstance()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await TaskOf(host.Instance("EchoPlugin"), "Started").WaitAsync(TimeSpan.FromSeconds(10));
		await using McpClient client = await host.ConnectAsync("Echo");
		string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.slnx");

		CallToolResult result = await client.CallToolAsync("echo_model", new Dictionary<string, object?> { ["solutionPath"] = missing });

		Assert.StartsWith("started=True status=Faulted error=No solution file", TextOf(result));
	}

	[Fact]
	public async Task WhenAPluginsToolThrows_ThenItFailsTheWayRoslynksToolsDoWithTheToolsMessage()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		await using McpClient client = await host.ConnectAsync("Echo");

		CallToolResult result = await client.CallToolAsync("explode");

		string text = TextOf(result);
		Assert.Contains("error=", text);
		Assert.Contains("The tool's own failure.", text);
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

		using HttpResponseMessage response = await PostAsync(host, PluginEndpoints.PathFor("Missing"), """{"jsonrpc":"2.0","id":1,"method":"ping"}""");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Contains("No plugin named 'Missing'", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task WhenOnePluginsToolsCannotBeCreated_ThenItsEndpointSaysWhyAndTheOthersStillServe()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Duplicate", DuplicateToolsPlugin), ("Echo", EchoPlugin));

		using HttpResponseMessage broken = await PostAsync(host, PluginEndpoints.PathFor("Duplicate"), """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
		await using McpClient echo = await host.ConnectAsync("Echo");

		Assert.Equal(HttpStatusCode.NotFound, broken.StatusCode);
		Assert.Contains("lookup", await broken.Content.ReadAsStringAsync());
		Assert.Contains(host.Log.Entries, entry => entry.StartsWith("Warning: Plugin serves no tools: Duplicate: ", StringComparison.Ordinal));
		Assert.Equal(2, (await echo.ListToolsAsync()).Count);
	}

	[Fact]
	public async Task WhenAPluginsEndpointIsSentInvalidJson_ThenTheParsersDiagnosisIsReturned()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));

		using HttpResponseMessage response = await PostAsync(host, PluginEndpoints.PathFor("Echo"), """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"path":"C:\Tmp"}}""");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("forward slashes (C:/Data)", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task WhenOnePluginFails_ThenItIsLoggedAndTheOthersKeepRunningUntilTheDaemonStops()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(("Echo", EchoPlugin));
		object echo = host.Instance("EchoPlugin");
		await TaskOf(host.Instance("FailingPlugin"), "Started").WaitAsync(TimeSpan.FromSeconds(10));
		await TaskOf(echo, "Started").WaitAsync(TimeSpan.FromSeconds(10));

		await WaitForAsync(() => host.Log.Entries.Any(entry => entry.Contains("Plugin Echo (Echo.FailingPlugin) stopped with an exception", StringComparison.Ordinal)));
		Assert.False(TaskOf(echo, "Stopped").IsCompleted);

		await host.StopAsync();

		await TaskOf(echo, "Stopped").WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task WhenAPluginIgnoresCancellation_ThenTheDaemonStopsWithinTheBudgetAndSaysSo()
	{
		await using PluginTestHost host = await PluginTestHost.StartAsync(TimeSpan.FromSeconds(1), ("Stubborn", StubbornPlugin));
		await TaskOf(host.Instance("StubbornPlugin"), "Started").WaitAsync(TimeSpan.FromSeconds(10));

		var stopwatch = Stopwatch.StartNew();
		await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

		Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
		Assert.Contains(host.Log.Entries, entry => entry.Contains("Plugin Stubborn (Stubborn.StubbornPlugin) did not stop within", StringComparison.Ordinal));
	}

	private static async Task<HttpResponseMessage> PostAsync(PluginTestHost host, string path, string body)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};
		request.Headers.Accept.ParseAdd("application/json, text/event-stream");
		return await host.Client.SendAsync(request);
	}

	private static string TextOf(CallToolResult result) =>
		Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

	private static Task TaskOf(object plugin, string property)
	{
		object source = plugin.GetType().GetProperty(property)!.GetValue(plugin)!;
		return (Task)source.GetType().GetProperty("Task")!.GetValue(source)!;
	}

	private static async Task WaitForAsync(Func<bool> condition)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
				Assert.Fail("The condition was not met within 10 seconds.");
			await Task.Delay(25);
		}
	}
}
