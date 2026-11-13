using System.Reflection;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The assembly-loading seam plugin loading depends on, so tests can stage candidates without
/// loading real PEs. The production implementation loads the entry assembly into its own
/// (non-collectible) <see cref="PluginAssemblyLoadContext"/>.
/// </summary>
public interface IAssemblyLoader
{
	/// <summary>
	/// Loads the candidate's entry assembly. A failure throws; the loader maps the exception onto
	/// its failure stage and records it — the folder stays in place and the next start re-examines it.
	/// </summary>
	Assembly Load(string pluginName, string entryAssemblyPath, string? runtimeDirectory);
}

/// <summary>The production implementation of <see cref="IAssemblyLoader"/>.</summary>
public sealed class PluginAssemblyLoader : IAssemblyLoader
{
	public Assembly Load(string pluginName, string entryAssemblyPath, string? runtimeDirectory)
	{
		var loadContext = new PluginAssemblyLoadContext(pluginName, entryAssemblyPath, runtimeDirectory);
		return loadContext.LoadFromAssemblyPath(entryAssemblyPath);
	}
}
