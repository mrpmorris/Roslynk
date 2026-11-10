using System.Reflection;
using System.Runtime.Loader;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The (non-collectible) load context a plugin assembly and its dependencies live in. Nothing is
/// ever unloaded — there is no unloading-on-error — so the context lives as long as the process.
///
/// Resolution order for the plugin's dependencies: Roslynk's own binaries first (a null return
/// defers to the default context, whose copies of the contract and of Microsoft.CodeAnalysis the
/// plugin must share for type identity), then the plugin's deployment folder via its deps.json,
/// then the plugin directory itself, then the default probing.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
	private readonly string PluginDirectory;
	private readonly string? RuntimeDirectory;
	private readonly AssemblyDependencyResolver? Resolver;

	public string PluginName { get; }

	public PluginLoadContext(string pluginName, string entryAssemblyPath, string? runtimeDirectory = null)
		: base(name: $"Roslynk.Plugin:{pluginName}", isCollectible: false)
	{
		PluginName = pluginName;
		PluginDirectory = Path.GetDirectoryName(entryAssemblyPath) ?? ".";
		RuntimeDirectory = runtimeDirectory;
		Resolver = new AssemblyDependencyResolver(entryAssemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is not { } name)
			return null;

		// The host's copy wins: bind to exactly the assembly Roslynk itself runs on. Returning null
		// hands the bind to the default context, which owns those assemblies. Loading a second copy
		// here would break type identity for IPluginContext and every Roslyn type.
		if (RuntimeDirectory is { } runtimeDirectory && File.Exists(Path.Combine(runtimeDirectory, $"{name}.dll")))
			return null;

		if (Resolver?.ResolveAssemblyToPath(assemblyName) is { } resolvedPath)
			return LoadFromAssemblyPath(resolvedPath);

		string candidate = Path.Combine(PluginDirectory, $"{name}.dll");
		return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
	}

	protected override nint LoadUnmanagedDll(string unmanagedDllName)
	{
		if (Resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) is { } resolvedPath)
			return LoadUnmanagedDllFromPath(resolvedPath);

		return nint.Zero;
	}
}
