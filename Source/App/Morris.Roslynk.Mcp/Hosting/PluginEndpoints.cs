using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Each plugin's MCP endpoint, <c>/plugins/&lt;name&gt;</c>: the same MCP server as Roslynk's own endpoint, with
/// the plugin's tools (<see cref="PluginToolSets"/>) in place of Roslynk's. A plugin's tool classes are created
/// per call from the daemon's services, so they can take <see cref="Morris.Roslynk.Plugins.ISolutionProvider"/>
/// and the plugin's own <see cref="Morris.Roslynk.Plugins.IRoslynkPlugin"/> instances, by their concrete types,
/// in their constructors.
/// </summary>
public static class PluginEndpoints
{
	public const string RouteParameter = "plugin";
	public const string PathPrefix = "/plugins";
	public const string RoutePattern = PathPrefix + "/{" + RouteParameter + "}";

	/// <summary>How long the daemon waits at shutdown for plugins to stop, within the bridge's own wait for an old daemon to exit.</summary>
	public static readonly TimeSpan DefaultStopBudget = TimeSpan.FromSeconds(5);

	/// <summary>The path of <paramref name="pluginName"/>'s endpoint.</summary>
	public static string PathFor(string pluginName) => PathPrefix + "/" + Uri.EscapeDataString(pluginName);

	/// <summary>Registers the plugins, their tools, the service that runs them, and their instances for their tools to take.</summary>
	public static IServiceCollection AddRoslynkPlugins(this IServiceCollection services, PluginCatalog catalog, TimeSpan? stopBudget = null)
	{
		ArgumentNullException.ThrowIfNull(catalog);

		services.AddSingleton(catalog);
		foreach (LoadedPlugin plugin in catalog.Plugins)
		{
			foreach (var instance in plugin.Instances)
				services.AddSingleton(instance.GetType(), instance);
		}

		services.AddSingleton(provider => new PluginToolSets(catalog, provider, provider.GetService<ResponseBudget>()));
		services.AddHostedService(provider => ActivatorUtilities.CreateInstance<PluginHostService>(provider, stopBudget ?? DefaultStopBudget));
		return services;
	}

	/// <summary>
	/// For <see cref="HttpServerTransportOptions.ConfigureSessionOptions"/>: a session opened on a plugin's endpoint
	/// gets that plugin's tools and no others; a session on Roslynk's own endpoint is left as it is.
	/// </summary>
	public static Task ConfigureSessionOptionsAsync(HttpContext context, McpServerOptions options, CancellationToken cancellationToken)
	{
		if (context.GetRouteValue(RouteParameter) is not string pluginName)
			return Task.CompletedTask;

		PluginToolSets toolSets = context.RequestServices.GetRequiredService<PluginToolSets>();
		options.ToolCollection = toolSets.For(pluginName) ?? [];
		options.ServerInfo = new Implementation
		{
			Name = $"Roslynk plugin {pluginName}",
			Title = $"Roslynk plugin {pluginName}",
			Version = options.ServerInfo?.Version ?? "1.0.0",
		};
		options.ServerInstructions = null;
		return Task.CompletedTask;
	}

	/// <summary>
	/// Maps <c>/plugins/&lt;name&gt;</c>. A name no plugin has, or a plugin whose tools could not be created, gets
	/// 404 with the reason before any MCP handling.
	/// </summary>
	public static IEndpointConventionBuilder MapRoslynkPlugins(this IEndpointRouteBuilder endpoints) =>
		endpoints.MapMcp(RoutePattern).AddEndpointFilter(async (context, next) =>
		{
			IServiceProvider services = context.HttpContext.RequestServices;
			string name = context.HttpContext.GetRouteValue(RouteParameter) as string ?? "";

			PluginCatalog catalog = services.GetRequiredService<PluginCatalog>();
			if (catalog.Find(name) is null)
				return Results.NotFound(catalog.ErrorFor(name) is string loadError
					? $"Plugin '{name}' did not load: {loadError}"
					: $"No plugin named '{name}' is loaded.");

			if (services.GetRequiredService<PluginToolSets>().ErrorFor(name) is string error)
				return Results.NotFound($"Plugin '{name}' serves no tools: {error}");

			return await next(context);
		});
}
