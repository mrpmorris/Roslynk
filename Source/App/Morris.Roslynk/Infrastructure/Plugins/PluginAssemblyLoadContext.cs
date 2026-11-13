using System.Reflection;
using System.Runtime.Loader;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The (non-collectible) load context one plugin and its private dependencies live in. Nothing is
/// ever unloaded — there is no unloading-on-error — so the context lives as long as the process, and
/// process exit reclaims everything it holds.
///
/// Resolution follows the house pattern of <see cref="Morris.Roslynk.Infrastructure.Workspaces.ShadowCopyingAnalyzerAssemblyLoader"/>
/// (no <c>Load</c> override, a <c>Resolving</c> event): every assembly name the default context can
/// supply binds there first, version-insensitively — the host's copies of Microsoft.CodeAnalysis.*,
/// ModelContextProtocol.*, Microsoft.Extensions.* and the contract assembly
/// Morris.Roslynk.Plugins.Abstractions win over any copy a plugin deployed beside itself, so both
/// sides share one type identity. The <see cref="Resolving"/> event then fills in what the default
/// context cannot supply, in this order: the host's loaded assemblies (any version), the Roslynk
/// binaries folder, the plugin's own folder via <see cref="AssemblyDependencyResolver"/>, and finally
/// the plugin's directory itself (hand-deployed plugins carry no deps.json for the resolver to read).
/// A name nothing satisfies fails the bind — the loader reports it as dependency-unresolved.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
	private readonly string PluginDirectory;
	private readonly string? RuntimeDirectory;
	private readonly AssemblyDependencyResolver? Resolver;

	public PluginAssemblyLoadContext(string pluginName, string entryAssemblyPath, string? runtimeDirectory = null)
		: base(name: $"Roslynk.Plugin:{pluginName}", isCollectible: false)
	{
		PluginDirectory = Path.GetDirectoryName(entryAssemblyPath) ?? ".";
		RuntimeDirectory = runtimeDirectory;
		try
		{
			Resolver = new AssemblyDependencyResolver(entryAssemblyPath);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or BadImageFormatException)
		{
			// No deps.json (or an unreadable one) is expected for hand-deployed plugins; the
			// directory fallback below still resolves assemblies sitting beside the entry assembly.
			Resolver = null;
		}

		Resolving += ResolveDependency;
	}

	private Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName assemblyName)
	{
		if (assemblyName.Name is not { } name)
			return null;

		// The host's loaded assemblies: any name the host has loaded resolves to the host's copy
		// regardless of the requested version — ALCs have no binding redirects, so forwarding is
		// implemented here.
		Assembly? hostAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
			candidate => string.Equals(candidate.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
		if (hostAssembly is not null)
			return hostAssembly;

		// The Roslynk binaries folder, which carries the contract assembly; requests for it resolve
		// to the host's copy — never to the copy sitting in the plugin's own folder.
		if (RuntimeDirectory is { } runtimeDirectory)
		{
			string runtimeCandidate = Path.Combine(runtimeDirectory, name + ".dll");
			if (File.Exists(runtimeCandidate))
				return context.LoadFromAssemblyPath(runtimeCandidate);
		}

		if (Resolver?.ResolveAssemblyToPath(assemblyName) is { } resolvedPath)
			return context.LoadFromAssemblyPath(resolvedPath);

		// A hand-deployed plugin carries no deps.json; assemblies sitting beside its entry
		// assembly still resolve.
		string sameDirectory = Path.Combine(PluginDirectory, name + ".dll");
		return File.Exists(sameDirectory) ? context.LoadFromAssemblyPath(sameDirectory) : null;
	}

	protected override nint LoadUnmanagedDll(string unmanagedDllName)
	{
		if (Resolver?.ResolveUnmanagedDllToPath(unmanagedDllName) is { } resolvedPath)
			return LoadUnmanagedDllFromPath(resolvedPath);

		return nint.Zero;
	}
}
