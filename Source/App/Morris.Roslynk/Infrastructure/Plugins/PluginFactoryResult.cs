using System.Reflection;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The outcome of finding a plugin's factory method. <see cref="FactoryMethod"/> is set exactly
/// when the search succeeded; on failure <see cref="Stage"/> and <see cref="Reason"/> name the
/// stage and cause.
/// </summary>
public sealed record PluginFactoryResult(
	string PluginName,
	MethodInfo? FactoryMethod,
	PluginDiscoveryStage? Stage,
	string? Reason)
{
	public bool IsSuccess => FactoryMethod is not null;

	public static PluginFactoryResult Succeeded(string pluginName, MethodInfo factoryMethod) =>
		new(pluginName, factoryMethod, null, null);

	public static PluginFactoryResult Failed(string pluginName, PluginDiscoveryStage stage, string reason) =>
		new(pluginName, null, stage, reason);
}
