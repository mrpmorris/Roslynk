using ModelContextProtocol.Server;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The plugin tools registered so far, process-wide. The MCP SDK rebuilds its per-session options
/// from <c>IConfigureOptions&lt;McpServerOptions&gt;</c> registrations, so a collection mutated
/// after the singleton options value was created is not seen by later sessions;
/// <see cref="PluginsOptionsSetup"/> copies this store into every options instance as it is built.
/// </summary>
internal sealed class PluginToolStore
{
	private readonly System.Collections.Concurrent.ConcurrentDictionary<McpServerTool, byte> Tools = new();

	public void Add(McpServerTool tool) => Tools.TryAdd(tool, 0);

	public void Remove(McpServerTool tool) => Tools.TryRemove(tool, out _);

	public IReadOnlyList<McpServerTool> All() => [.. Tools.Keys];
}

/// <summary>
/// Adds every registered plugin tool to each <see cref="McpServerOptions"/> instance the options
/// factory builds — the singleton value and every scoped snapshot. Runs alongside the SDK's own
/// setup, which fills the collection from the DI-registered built-in tools.
/// </summary>
internal sealed class PluginsOptionsSetup : Microsoft.Extensions.Options.IConfigureOptions<McpServerOptions>
{
	private readonly PluginToolStore Store;

	public PluginsOptionsSetup(PluginToolStore store)
	{
		Store = store;
	}

	public void Configure(McpServerOptions options)
	{
		foreach (McpServerTool tool in Store.All())
		{
			options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
			options.ToolCollection.TryAdd(tool);
		}
	}
}
