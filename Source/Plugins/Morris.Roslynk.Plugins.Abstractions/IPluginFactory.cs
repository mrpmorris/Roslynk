namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>
/// The factory claim. A plugin names its factory with the assembly-level
/// <see cref="PluginFactoryAttribute{T}"/>; the static abstract member makes the factory's shape a
/// compile error in the plugin author's build when it is wrong — a wrong signature, a non-implementing
/// type or a missing attribute argument cannot compile.
/// </summary>
public interface IPluginFactory
{
	/// <summary>
	/// Creates the plugin instance, or returns null when the plugin's own initialization fails.
	/// A null return ends that plugin's run for this daemon start; the next start invokes the
	/// factory again.
	/// </summary>
	static abstract IPlugin? Create(IPluginContext context);
}
