using System.Reflection;
using System.Runtime.Loader;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads one plugin and its own dependencies in isolation, except any assembly the daemon itself has: those
/// (the plugin contract, Roslyn, the MCP SDK, the framework) come from the daemon, so a <c>Solution</c> or tool
/// type crossing between them is the same type on both sides, and two plugins can use different versions of
/// anything else.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
	private readonly AssemblyDependencyResolver Resolver;

	public PluginLoadContext(string pluginAssemblyPath)
		: base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
	{
		Resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (IsTheDaemons(assemblyName))
			return null;

		string? path = Resolver.ResolveAssemblyToPath(assemblyName);
		return path is null ? null : LoadFromAssemblyPath(path);
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
	{
		string? path = Resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
	}

	/// <summary>
	/// Whether the daemon has <paramref name="assemblyName"/>: already loaded, beside its executable, or in the
	/// framework it runs on. Returning null for these defers to the default context, which loads the daemon's copy.
	/// </summary>
	private static bool IsTheDaemons(AssemblyName assemblyName)
	{
		string? name = assemblyName.Name;
		if (name is null)
			return false;

		if (Default.Assemblies.Any(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
			return true;

		if (File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")))
			return true;

		return AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted
			&& trusted.Split(Path.PathSeparator).Any(path => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));
	}
}
