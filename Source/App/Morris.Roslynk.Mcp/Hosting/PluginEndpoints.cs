using System.Reflection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Each plugin's MCP endpoint, <c>/plugins/&lt;name&gt;</c>: the same MCP server as Roslynk's own endpoint, with
/// the plugin's tools in place of Roslynk's. A plugin's tool classes are created per call from the daemon's
/// services, so they can take <see cref="Morris.Roslynk.Plugins.ISolutionProvider"/> and the plugin's own
/// <see cref="Morris.Roslynk.Plugins.IRoslynkPlugin"/> instances (by their concrete types) in their constructors.
/// </summary>
public static class PluginEndpoints
{
	public const string RouteParameter = "plugin";
	public const string RoutePattern = "/plugins/{" + RouteParameter + "}";

	/// <summary>The path of <paramref name="pluginName"/>'s endpoint.</summary>
	public static string PathFor(string pluginName) => "/plugins/" + Uri.EscapeDataString(pluginName);

	/// <summary>Registers the plugins, the service that runs them, and their instances for their tools to take.</summary>
	public static IServiceCollection AddRoslynkPlugins(this IServiceCollection services, PluginCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);

		services.AddSingleton(catalog);
		foreach (LoadedPlugin plugin in catalog.Plugins)
		{
			foreach (var instance in plugin.Instances)
				services.AddSingleton(instance.GetType(), instance);
		}

		services.AddSingleton<PluginToolSets>();
		services.AddHostedService<PluginHostService>();
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
		options.ToolCollection = toolSets.For(pluginName);
		options.ServerInfo = new Implementation
		{
			Name = $"Roslynk plugin {pluginName}",
			Version = options.ServerInfo?.Version ?? "1.0.0",
		};
		options.ServerInstructions = null;
		return Task.CompletedTask;
	}

	/// <summary>Maps <c>/plugins/&lt;name&gt;</c>; an unknown name gets 404 before any MCP handling.</summary>
	public static IEndpointConventionBuilder MapRoslynkPlugins(this IEndpointRouteBuilder endpoints) =>
		endpoints.MapMcp(RoutePattern).AddEndpointFilter(async (context, next) =>
		{
			PluginCatalog catalog = context.HttpContext.RequestServices.GetRequiredService<PluginCatalog>();
			return context.HttpContext.GetRouteValue(RouteParameter) is string name && catalog.Find(name) is not null
				? await next(context)
				: Results.NotFound();
		});
}

/// <summary>Each plugin's MCP tools, created once from its tool types.</summary>
public sealed class PluginToolSets
{
	private readonly Dictionary<string, McpServerPrimitiveCollection<McpServerTool>> ToolsByPlugin = new(StringComparer.OrdinalIgnoreCase);

	public PluginToolSets(PluginCatalog catalog, IServiceProvider services)
	{
		foreach (LoadedPlugin plugin in catalog.Plugins)
		{
			var tools = new McpServerPrimitiveCollection<McpServerTool>();
			foreach (Type toolType in plugin.ToolTypes)
			{
				foreach (MethodInfo method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
				{
					if (!method.IsDefined(typeof(McpServerToolAttribute), inherit: false))
						continue;

					var options = new McpServerToolCreateOptions { Services = services };
					tools.Add(method.IsStatic
						? McpServerTool.Create(method, target: null, options)
						: McpServerTool.Create(method, request => ActivatorUtilities.CreateInstance(request.Services ?? services, toolType), options));
				}
			}

			ToolsByPlugin[plugin.Name] = tools;
		}
	}

	/// <summary>The tools of <paramref name="pluginName"/>; none for a name no plugin has.</summary>
	public McpServerPrimitiveCollection<McpServerTool> For(string pluginName) =>
		ToolsByPlugin.TryGetValue(pluginName, out var tools) ? tools : [];
}
