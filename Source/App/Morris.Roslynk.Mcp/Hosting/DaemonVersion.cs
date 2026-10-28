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

	/// <summary>Recognizes local builds, including informational-version commit metadata.</summary>
	public static bool IsDevelopment(string? version) =>
		string.Equals(version?.Split('+', 2)[0], "0.0.1-local", StringComparison.OrdinalIgnoreCase);

	private static string Resolve()
	{
		Assembly assembly = typeof(DaemonVersion).Assembly;

		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "unknown";
	}
}
