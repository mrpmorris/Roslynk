using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads plugins during daemon startup, before MCP request acceptance: discovery walks the plugins
/// root, each candidate runs the load pipeline serially within its per-plugin startup budget, and
/// registered tools join the same tool collection as the built-ins (one MCP connection, one
/// tools/list). Shutdown runs the spec's sequence: stop accepting new plugin tool calls, signal
/// shutdown cancellation on every in-flight call's token and wait for those calls, end event delivery
/// (publication sources quiesced, OnCompleted the final queue item), and wait for the pump to drain.
/// A handler or tool that never returns stalls the wait — the accepted mirror of the delivery stall.
/// </summary>
internal sealed class PluginHostedService : IHostedService
{
	private readonly PluginDiscovery Discovery;
	private readonly PluginLoader Loader;
	private readonly SolutionEventPublisher SolutionEvents;
	private readonly PluginToolTracker Tracker;
	private readonly IPluginToolSink ToolSink;
	private readonly IHostApplicationLifetime Lifetime;
	private readonly ILogger<PluginHostedService> Logger;

	public PluginHostedService(
		PluginDiscovery discovery,
		PluginLoader loader,
		SolutionEventPublisher solutionEvents,
		PluginToolTracker tracker,
		IPluginToolSink toolSink,
		IHostApplicationLifetime lifetime,
		ILogger<PluginHostedService> logger)
	{
		Discovery = discovery;
		Loader = loader;
		SolutionEvents = solutionEvents;
		Tracker = tracker;
		ToolSink = toolSink;
		Lifetime = lifetime;
		Logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// In-flight plugin tool calls unwind as early as the shutdown signal, not when this service's
		// StopAsync runs; the full shutdown sequence still runs there, before the process exits.
		Lifetime.ApplicationStopping.Register(() => Tracker.Shutdown.BeginShutdown());

		PluginDiscoveryResult discovery = Discovery.Discover();
		return LoadAllAsync(discovery.Candidates, cancellationToken);
	}

	private async Task LoadAllAsync(IReadOnlyList<PluginCandidate> candidates, CancellationToken cancellationToken)
	{
		// Startup is serial per candidate: the daemon accepts MCP requests once every candidate's
		// load has run (bounded by the per-plugin startup budgets plus any in-flight overshoot).
		foreach (PluginCandidate candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await Loader.LoadAsync(candidate, cancellationToken);
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		// 1-2. Stop accepting new plugin tool calls, signal shutdown cancellation on every in-flight
		// call's token, and wait for those calls to finish (the ApplicationStopping registration
		// cancelled them already; this is the wait).
		await Tracker.BeginShutdownAndWaitAsync();

		// 4. End event delivery: publications are quiesced, OnCompleted is enqueued after every
		// pending event, and the pump drains before the process exits. Solutions unload with the
		// process; the unloading publishes no events.
		await SolutionEvents.CompleteAsync();
	}
}

/// <summary>The production tool sink: the MCP server's tool collection plus the store every options
/// instance is built from.</summary>
internal sealed class McpToolCollectionSink : IPluginToolSink
{
	private readonly McpServerPrimitiveCollection<McpServerTool> Collection;
	private readonly PluginToolStore Store;

	public McpToolCollectionSink(McpServerOptions options, PluginToolStore store)
	{
		Collection = options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
		Store = store;
	}

	public void Add(McpServerTool tool)
	{
		Store.Add(tool);
		Collection.TryAdd(tool);
	}

	public void Remove(McpServerTool tool)
	{
		Store.Remove(tool);
		Collection.Remove(tool);
	}
}
