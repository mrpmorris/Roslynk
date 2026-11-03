using System.Collections.Concurrent;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Runs every loaded plugin's <see cref="IRoslynkPlugin.RunAsync"/> for the life of the daemon and cancels them
/// when it stops. A plugin that throws is logged and left stopped; the daemon and the other plugins carry on. At
/// shutdown each plugin gets <c>stopBudget</c> to finish; one that ignores cancellation is logged and left behind,
/// so it cannot hold the daemon past a newer bridge's wait for it to exit.
/// </summary>
public sealed class PluginHostService : BackgroundService
{
	private readonly PluginCatalog Catalog;
	private readonly PluginToolSets ToolSets;
	private readonly ISolutionProvider SolutionProvider;
	private readonly ILogger<PluginHostService> Logger;
	private readonly TimeSpan StopBudget;
	private readonly ConcurrentDictionary<IRoslynkPlugin, string> Running = new(ReferenceEqualityComparer.Instance);

	public PluginHostService(
		PluginCatalog catalog,
		PluginToolSets toolSets,
		ISolutionProvider solutionProvider,
		ILogger<PluginHostService> logger,
		TimeSpan stopBudget)
	{
		Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		ToolSets = toolSets ?? throw new ArgumentNullException(nameof(toolSets));
		SolutionProvider = solutionProvider ?? throw new ArgumentNullException(nameof(solutionProvider));
		Logger = logger ?? throw new ArgumentNullException(nameof(logger));
		StopBudget = stopBudget;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		foreach (string error in Catalog.Errors)
			Logger.LogWarning("Plugin not loaded: {Error}", error);

		foreach (string error in ToolSets.Errors)
			Logger.LogWarning("Plugin serves no tools: {Error}", error);

		foreach (LoadedPlugin plugin in Catalog.Plugins)
			Logger.LogInformation("Plugin {Plugin} loaded: {Instances} to run, {ToolTypes} tool types", plugin.Name, plugin.Instances.Count, plugin.ToolTypes.Count);

		return Task.WhenAll(Catalog.Plugins.SelectMany(plugin => plugin.Instances.Select(instance => RunAsync(plugin.Name, instance, stoppingToken))));
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(StopBudget);

		// Returns when every plugin has stopped or the budget is spent, whichever is first.
		await base.StopAsync(budget.Token);

		foreach ((IRoslynkPlugin instance, string pluginName) in Running)
			Logger.LogWarning("Plugin {Plugin} ({Type}) did not stop within {Budget}; the daemon stops without waiting for it", pluginName, instance.GetType().FullName, StopBudget);
	}

	private async Task RunAsync(string pluginName, IRoslynkPlugin instance, CancellationToken stoppingToken)
	{
		Running[instance] = pluginName;

		// Off the host's startup path: a plugin that does synchronous work before its first await would
		// otherwise hold up every service started after this one.
		await Task.Yield();
		try
		{
			await instance.RunAsync(SolutionProvider, stoppingToken);
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			Logger.LogError(exception, "Plugin {Plugin} ({Type}) stopped with an exception", pluginName, instance.GetType().FullName);
		}
		finally
		{
			Running.TryRemove(instance, out _);
		}
	}
}
