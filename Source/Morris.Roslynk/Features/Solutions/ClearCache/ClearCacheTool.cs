using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Features.Solutions.ClearCache;

[McpServerToolType]
public sealed class ClearCacheTool
{
	public const string ClearCacheName = "clear_cache";

	private readonly InstanceRegistry InstanceRegistry;

	public ClearCacheTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = ClearCacheName,
		Title = "Clear all loaded solutions",
		ReadOnly = false,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description("Unloads every solution from memory, freeing all workspaces. No effect on files. Returns '#cleared=<count evicted>'.")]
	public string ClearCache()
	{
		return new OutlineBuilder().Header("cleared", InstanceRegistry.ClearAll()).ToString();
	}
}
