using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Runs every loaded plugin's <see cref="IRoslynkPlugin.RunAsync"/> for the life of the daemon and cancels them
/// when it stops. A plugin that throws is logged and left stopped; the daemon and the other plugins carry on.
/// </summary>
public sealed class PluginHostService : BackgroundService
{
	private readonly PluginCatalog Catalog;
	private readonly ISolutionProvider SolutionProvider;
	private readonly ILogger<PluginHostService> Logger;

	public PluginHostService(PluginCatalog catalog, ISolutionProvider solutionProvider, ILogger<PluginHostService> logger)
	{
		Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		SolutionProvider = solutionProvider ?? throw new ArgumentNullException(nameof(solutionProvider));
		Logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
	{
		foreach (string error in Catalog.Errors)
			Logger.LogWarning("Plugin not loaded: {Error}", error);

		foreach (LoadedPlugin plugin in Catalog.Plugins)
			Logger.LogInformation("Plugin {Plugin} loaded: {Instances} to run, {ToolTypes} tool types", plugin.Name, plugin.Instances.Count, plugin.ToolTypes.Count);

		return Task.WhenAll(Catalog.Plugins.SelectMany(plugin => plugin.Instances.Select(instance => RunAsync(plugin.Name, instance, stoppingToken))));
	}

	private async Task RunAsync(string pluginName, IRoslynkPlugin instance, CancellationToken stoppingToken)
	{
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
	}
}
