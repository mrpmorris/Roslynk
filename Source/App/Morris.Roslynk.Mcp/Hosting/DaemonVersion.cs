using System.Reflection;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The version the running host identifies itself with. The informational version is what the package
/// carries (CI overrides <c>Version</c> from the release tag), so a bridge built from one package and a
/// daemon started from another can tell each other apart.
/// </summary>
public static class DaemonVersion
{
	public static string Current { get; } = Resolve();

	private static string Resolve()
	{
		Assembly assembly = typeof(DaemonVersion).Assembly;

		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "unknown";
	}
}
