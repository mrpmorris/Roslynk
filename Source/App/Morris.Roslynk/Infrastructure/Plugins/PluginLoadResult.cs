using System.Reflection;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The outcome of loading a plugin entry assembly. <see cref="Assembly"/> is set exactly when the
/// load succeeded; on failure <see cref="Stage"/> and <see cref="Reason"/> name the stage and cause.
/// </summary>
public sealed record PluginLoadResult(
	string PluginName,
	Assembly? Assembly,
	PluginLoadContext? LoadContext,
	PluginDiscoveryStage? Stage,
	string? Reason)
{
	public bool IsSuccess => Assembly is not null;

	public static PluginLoadResult Succeeded(string pluginName, Assembly assembly, PluginLoadContext loadContext) =>
		new(pluginName, assembly, loadContext, null, null);

	public static PluginLoadResult Failed(string pluginName, PluginDiscoveryStage stage, string reason) =>
		new(pluginName, null, null, stage, reason);
}
