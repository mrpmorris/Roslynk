using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads plugins during daemon startup: discovery walks the plugins root once the host has started —
/// after Kestrel is listening, so the port answers and the handshake's version probe succeeds during
/// the startup window — each candidate runs the load pipeline serially within its per-plugin startup
/// budget, and registered tools join the same tool collection as the built-ins (one MCP connection,
/// one tools/list). MCP requests wait on the <see cref="PluginStartupGate"/> until every candidate's
/// load has run, so none is accepted before startup completes; the daemon endpoints stay ungated and
/// answer during the window. Shutdown runs the spec's sequence: stop accepting new plugin tool calls,
/// signal shutdown cancellation on every in-flight call's token and wait for those calls, end event
/// delivery (publication sources quiesced, OnCompleted the final queue item), and wait for the pump
/// to drain. A handler or tool that never returns stalls the wait — the accepted mirror of the
/// delivery stall.
/// </summary>
internal sealed class PluginHostedService : IHostedService
{
	private readonly PluginDiscovery Discovery;
	private readonly PluginLoader Loader;
	private readonly SolutionEventPublisher SolutionEvents;
	private readonly PluginToolTracker Tracker;
	private readonly PluginStartupGate StartupGate;
	private readonly IHostApplicationLifetime Lifetime;
	private Task? LoadTask;

	public PluginHostedService(
		PluginDiscovery discovery,
		PluginLoader loader,
		SolutionEventPublisher solutionEvents,
		PluginToolTracker tracker,
		PluginStartupGate startupGate,
		IHostApplicationLifetime lifetime)
	{
		Discovery = discovery;
		Loader = loader;
		SolutionEvents = solutionEvents;
		Tracker = tracker;
		StartupGate = startupGate;
		Lifetime = lifetime;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// In-flight plugin tool calls unwind as early as the shutdown signal, not when this service's
		// StopAsync runs; the full shutdown sequence still runs there, before the process exits.
		Lifetime.ApplicationStopping.Register(() => Tracker.Shutdown.BeginShutdown());

		// The load starts when the host has started and the port is listening; a shutdown during the
		// load cancels it, and the gate opens when the load ends either way, so no request waits on
		// a load that will never finish.
		var loadCancellation = new CancellationTokenSource();
		CancellationTokenRegistration stopRegistration = Lifetime.ApplicationStopping.Register(
			() => loadCancellation.Cancel());

		Lifetime.ApplicationStarted.Register(() =>
		{
			Task load = LoadTask = LoadAllAsync(Discovery.Discover().Candidates, loadCancellation.Token);
			_ = load.ContinueWith(
				_ =>
				{
					StartupGate.Open();
					stopRegistration.Dispose();
					loadCancellation.Dispose();
				},
				TaskScheduler.Default);
		});

		return Task.CompletedTask;
	}

	private async Task LoadAllAsync(IReadOnlyList<PluginCandidate> candidates, CancellationToken cancellationToken)
	{
		// Startup is serial per candidate: MCP requests are accepted once every candidate's load has
		// run (bounded by the per-plugin startup budgets plus any in-flight overshoot).
		try
		{
			foreach (PluginCandidate candidate in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await Loader.LoadAsync(candidate, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown arrived during the load; the remaining candidates are retried on the next start.
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		// An in-flight load ends with the shutdown signal; wait for it so the drain below runs after
		// every load has stopped touching shared state.
		if (LoadTask is { } load)
			await load;

		// 1-2. Stop accepting new plugin tool calls, signal shutdown cancellation on every in-flight
		// plugin tool call's token, and wait for those calls to finish (the ApplicationStopping
		// registration cancelled them already; this is the wait).
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
