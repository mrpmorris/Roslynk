using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk;

public static class ServicesRegistration
{
	/// <summary>
	/// Registers Roslynk's engine and feature services. Registrations are added here as each
	/// Infrastructure area and feature slice is built. The optional <paramref name="responseBudget"/> caps
	/// every tool result; hosts that read it from configuration pass it here (see the Roslynk MCP host's
	/// ResponseBudgetConfiguration), otherwise the default applies. The optional
	/// <paramref name="loadWaitTimeout"/> bounds how long solution-scoped tools wait for an in-flight initial
	/// load; hosts read 'Roslynk:LoadWaitSeconds' and pass the plain value, otherwise 30 seconds applies. The
	/// optional <paramref name="pluginStartupTimeout"/> bounds one plugin's startup (load through
	/// subscription); hosts read 'Roslynk:PluginStartupTimeout' and pass the plain value, otherwise 30
	/// seconds applies. The optional <paramref name="pluginsRoot"/> moves plugin discovery off
	/// '~/.roslynk/plugins' (tests stage their own roots). Plugin loading itself runs in a hosted service
	/// during daemon startup, before MCP request acceptance.
	/// </summary>
	public static IServiceCollection AddRoslynk(
		this IServiceCollection services,
		ResponseBudget? responseBudget = null,
		LoadWaitTimeout? loadWaitTimeout = null,
		TimeSpan? pluginStartupTimeout = null,
		string? pluginsRoot = null)
	{
		services.AddSingleton(responseBudget ?? ResponseBudget.Default);
		services.AddSingleton(loadWaitTimeout ?? LoadWaitTimeout.Default);
		services.AddSingleton(provider => new InstanceRegistry(
			provider.GetRequiredService<LoadWaitTimeout>(),
			provider.GetRequiredService<SolutionEventPublisher>()));
		services.AddSingleton<DiagnosticsService>();
		services.AddSingleton<SymbolResolver>();
		services.AddSingleton<ProjectionService>();
		services.AddSingleton<ConditionalCoverage>();
		services.AddSingleton<ApplyPipeline>();
		services.AddSingleton<DocumentDiagnosticsProvider>();
		services.AddSingleton<CodeActionService>();
		services.AddSingleton(provider => new SolutionMetrics(RoslynkMeter.Instance, provider.GetRequiredService<InstanceRegistry>()));

		// Plugins: the solution-event pump, the loader pipeline and its seams, and the runtime state
		// GET /plugins reports. The tool sink is the MCP server's tool collection, resolved when the
		// first tool is registered — after the host composition has run AddMcpServer.
		services.AddSingleton<PluginShutdownState>();
		services.AddSingleton<PluginToolTracker>();
		services.AddSingleton<PluginStartupGate>();
		services.AddSingleton<SolutionEventPublisher>();
		services.AddSingleton<LoadedPluginRegistry>();
		services.AddSingleton<PluginToolFactory>();
		services.AddSingleton<PluginContext>();
		services.AddSingleton<IFileSystem>(new DefaultFileSystem());
		services.AddSingleton<IAssemblyLoader, PluginAssemblyLoader>();
		services.AddSingleton(provider => PluginDiscovery.ForUserPluginsRoot(
			provider.GetRequiredService<IFileSystem>(),
			pluginsRoot,
			provider.GetService<ILogger<PluginDiscovery>>() ?? NullLogger<PluginDiscovery>.Instance));
		services.AddSingleton<IPluginToolSink>(provider => new McpToolCollectionSink(
			provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
			provider.GetRequiredService<PluginToolStore>()));
		// The SDK rebuilds per-session options from IConfigureOptions registrations; this setup adds
		// the plugin tools to every instance, so sessions created after registration see them.
		services.AddSingleton<PluginToolStore>();
		services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<McpServerOptions>, PluginsOptionsSetup>();
		services.AddSingleton(provider => new PluginLoader(
			provider.GetRequiredService<IFileSystem>(),
			provider.GetRequiredService<IAssemblyLoader>(),
			provider.GetRequiredService<PluginToolFactory>(),
			provider.GetRequiredService<IPluginToolSink>(),
			provider.GetRequiredService<PluginContext>(),
			provider.GetRequiredService<SolutionEventPublisher>(),
			provider.GetRequiredService<LoadedPluginRegistry>(),
			provider.GetRequiredService<InstanceRegistry>(),
			pluginStartupTimeout,
			logger: provider.GetService<ILogger<PluginLoader>>() ?? NullLogger<PluginLoader>.Instance));
		services.AddHostedService<PluginHostedService>();
		return services;
	}
}
