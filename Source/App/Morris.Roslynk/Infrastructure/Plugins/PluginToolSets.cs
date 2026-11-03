using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Each plugin's MCP tools, created once per plugin and wrapped as Roslynk's own tools are
/// (<see cref="RoslynkTool"/>), so a plugin's tools report failures and keep to the response budget the same
/// way. A plugin whose tools cannot be created (two tools with one name, say) is reported in
/// <see cref="Errors"/> and serves none; the other plugins are not affected.
/// </summary>
public sealed class PluginToolSets
{
	private readonly Dictionary<string, McpServerPrimitiveCollection<McpServerTool>> ToolsByPlugin = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> ErrorsByPlugin = new(StringComparer.OrdinalIgnoreCase);

	public PluginToolSets(PluginCatalog catalog, IServiceProvider services, ResponseBudget? budget = null)
	{
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(services);

		foreach (LoadedPlugin plugin in catalog.Plugins)
		{
			try
			{
				ToolsByPlugin[plugin.Name] = Create(plugin, services, budget);
			}
			catch (Exception exception)
			{
				ErrorsByPlugin[plugin.Name] = PluginCatalog.Reason(exception);
			}
		}
	}

	/// <summary>Why each plugin whose tools could not be created serves none, as <c>&lt;name&gt;: &lt;reason&gt;</c>.</summary>
	public IReadOnlyList<string> Errors => ErrorsByPlugin.Select(error => $"{error.Key}: {error.Value}").ToArray();

	/// <summary>The tools of <paramref name="pluginName"/>, or null when it has none because they could not be created.</summary>
	public McpServerPrimitiveCollection<McpServerTool>? For(string pluginName) =>
		ToolsByPlugin.TryGetValue(pluginName, out var tools) ? tools : null;

	/// <summary>Why <paramref name="pluginName"/>'s tools could not be created, or null.</summary>
	public string? ErrorFor(string pluginName) =>
		ErrorsByPlugin.TryGetValue(pluginName, out string? error) ? error : null;

	private static McpServerPrimitiveCollection<McpServerTool> Create(LoadedPlugin plugin, IServiceProvider services, ResponseBudget? budget)
	{
		var tools = new McpServerPrimitiveCollection<McpServerTool>();
		var options = new McpServerToolCreateOptions { Services = services };

		foreach (Type toolType in plugin.ToolTypes)
		{
			// Non-public too, as the SDK's own discovery (and so Roslynk's tools) does.
			foreach (MethodInfo method in toolType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
			{
				if (!method.IsDefined(typeof(McpServerToolAttribute), inherit: false))
					continue;

				McpServerTool tool = method.IsStatic
					? McpServerTool.Create(method, target: null, options)
					: McpServerTool.Create(method, request => ActivatorUtilities.CreateInstance(request.Services ?? services, toolType), options);
				tools.Add(new RoslynkTool(tool, budget));
			}
		}

		return tools;
	}
}
