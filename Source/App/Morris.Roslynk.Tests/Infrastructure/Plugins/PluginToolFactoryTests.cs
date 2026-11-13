using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginToolFactoryTests : IDisposable
{
	private readonly string Root;

	public PluginToolFactoryTests()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslynk-toolfactory-tests-" + Guid.NewGuid().ToString("N"));
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
			// The non-collectible load context keeps the entry DLL mapped until the process exits.
		}
	}

	private async Task<(PluginToolFactory Factory, CapturingLogger Captured, PluginToolTracker Tracker, IPlugin Plugin)> CreateFactoryAsync(string source, string instanceType = "Plugin")
	{
		var captured = new CapturingLogger();
		var tracker = new PluginToolTracker();
		var factory = new PluginToolFactory(new ResponseBudget(80_000), tracker, new WrappingLoggerFactory(captured));

		string folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(folder);
		string entry = TestPluginCompiler.Emit(folder, "ToolPlugin.Roslynk.Plugin", source, toolReference: true);
		var loadContext = new PluginAssemblyLoadContext("toolplugin", entry, AppContext.BaseDirectory);
		Assembly entryAssembly = loadContext.LoadFromAssemblyPath(entry);
		var plugin = (IPlugin)Activator.CreateInstance(entryAssembly.GetType(instanceType)!)!;
		return (factory, captured, tracker, plugin);
	}

	[Fact]
	public async Task WhenToolsAreDiscovered_ThenExposedNamesArePrefixedWithTheNormalizedPluginName()
	{
		(PluginToolFactory Factory, _, _, IPlugin Plugin) = await CreateFactoryAsync(TestPluginCompiler.ToolPluginSource);

		IReadOnlyList<McpServerTool> tools = Factory.CreateTools("toolplugin", Plugin);

		Assert.Equal(["toolplugin__First", "toolplugin__Second"], tools.Select(tool => tool.ProtocolTool.Name).ToArray());
	}

	[Fact]
	public async Task WhenTwoMethodsShareAnExposedName_ThenTheFirstWinsAndTheDuplicateIsRecordedNamingBothMethods()
	{
		(PluginToolFactory Factory, CapturingLogger Captured, _, IPlugin Plugin) = await CreateFactoryAsync(TestPluginCompiler.ToolPluginSource);

		IReadOnlyList<McpServerTool> tools = Factory.CreateTools("toolplugin", Plugin);

		Assert.Equal(2, tools.Count);
		var record = Assert.Single(Captured.Records, r => r.Level == LogLevel.Error);
		Assert.Contains("toolplugin__First", record.Message);
		Assert.Contains("Plugin.First", record.Message);
	}

	[Fact]
	public async Task WhenPublicMethodsAreInheritedFromBaseTypes_ThenTheyAreDiscovered()
	{
		(PluginToolFactory Factory, _, _, IPlugin Plugin) = await CreateFactoryAsync(TestPluginCompiler.ToolPluginSource, instanceType: "Derived");

		IReadOnlyList<McpServerTool> tools = Factory.CreateTools("toolplugin", Plugin);

		Assert.Contains(tools, tool => tool.ProtocolTool.Name == "toolplugin__Inherited");
		// The base type's public tools are inherited members of Derived and are discovered too.
		Assert.Contains(tools, tool => tool.ProtocolTool.Name == "toolplugin__First");
	}

	[Fact]
	public async Task WhenShutdownBegins_ThenTheTrackerSignalsItAndWaitsForInFlightCallsToFinish()
	{
		var tracker = new PluginToolTracker();
		tracker.Enter();

		Task wait = tracker.BeginShutdownAndWaitAsync();

		Assert.True(tracker.IsShuttingDown);
		Assert.False(wait.IsCompleted);

		tracker.Exit();
		await wait.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void WhenTheShutdownTokenIsCancelled_ThenALinkedRequestTokenFiresToo()
	{
		var tracker = new PluginToolTracker();
		using var request = new CancellationTokenSource();
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Token, tracker.ShutdownToken);

		tracker.Shutdown.BeginShutdown();

		Assert.True(linked.Token.IsCancellationRequested);
		Assert.False(request.Token.IsCancellationRequested);
	}

	private sealed class WrappingLoggerFactory : ILoggerFactory
	{
		private readonly CapturingLogger Captured;

		public WrappingLoggerFactory(CapturingLogger captured) => Captured = captured;

		public void AddProvider(ILoggerProvider provider) { }

		public ILogger CreateLogger(string categoryName) => Captured;

		public void Dispose() { }
	}
}
