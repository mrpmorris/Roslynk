using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads one plugin and its dependencies in isolation, except the assemblies whose types cross between the
/// daemon and a plugin: the plugin contract, Roslyn, the MCP SDK, Microsoft.Extensions and the .NET framework.
/// Those always come from the daemon, so a <c>Solution</c> or tool type is the same type on both sides. Anything
/// else comes from the plugin's own folder, so two plugins can use different versions of it.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
	private static readonly string[] SharedNames = ["Morris.Roslynk.Plugins"];
	private static readonly string[] SharedPrefixes = ["Microsoft.CodeAnalysis", "ModelContextProtocol", "Microsoft.Extensions."];

	/// <summary>The daemon's own assemblies by simple name: everything it was started with.</summary>
	private static readonly Lazy<Dictionary<string, string>> DaemonAssemblies = new(() =>
		(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.DistinctBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase));

	/// <summary>The folder holding the shared frameworks (<c>dotnet/shared</c>), whose assemblies are all shared.</summary>
	private static readonly string FrameworksFolder =
		Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)))! + Path.DirectorySeparatorChar;

	private static readonly ConcurrentDictionary<string, Version?> DaemonVersions = new(StringComparer.OrdinalIgnoreCase);

	private readonly AssemblyDependencyResolver Resolver;

	public PluginLoadContext(string pluginAssemblyPath)
		: base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: false)
	{
		Resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
	}

	/// <summary>
	/// Why <paramref name="assembly"/> cannot run in this daemon, or null: it references a shared assembly at a
	/// higher version than the daemon has, which the runtime would refuse at first use, far from the cause.
	/// </summary>
	public static string? CheckSharedVersions(Assembly assembly)
	{
		foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
		{
			if (reference.Name is not string name || reference.Version is not Version wanted || DaemonVersion(name) is not Version have)
				continue;

			if (wanted > have)
				return $"It was built against {name} {wanted}; this daemon has {have}. Build it against the versions the daemon ships.";
		}

		return null;
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		// Null defers to the default context, which loads the daemon's copy.
		if (assemblyName.Name is string name && DaemonVersion(name) is not null)
			return null;

		string? path = Resolver.ResolveAssemblyToPath(assemblyName);
		return path is null ? null : LoadFromAssemblyPath(path);
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
	{
		string? path = Resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
	}

	/// <summary>The daemon's version of <paramref name="name"/> if it is a shared assembly the daemon has; otherwise null.</summary>
	private static Version? DaemonVersion(string name) =>
		DaemonVersions.GetOrAdd(name, static name =>
			DaemonAssemblies.Value.TryGetValue(name, out string? path) && IsShared(name, path)
				? AssemblyName.GetAssemblyName(path).Version
				: null);

	private static bool IsShared(string name, string path) =>
		SharedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
		|| SharedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		|| path.StartsWith(FrameworksFolder, StringComparison.OrdinalIgnoreCase);
}
