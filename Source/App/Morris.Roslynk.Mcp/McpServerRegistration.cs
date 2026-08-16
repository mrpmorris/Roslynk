using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Mcp.Observability;

namespace Morris.Roslynk.Mcp;

/// <summary>
/// Server identity and the global instructions a client sees for the whole Roslynk tool surface.
/// </summary>
internal static class McpServerRegistration
{
	public static void Configure(McpServerOptions options)
	{
		options.ServerInfo = new Implementation
		{
			Name = "Roslynk",
			Title = "Roslynk; C# semantic intelligence",
			// Track the assembly informational version (set from Package/Version at build; CI injects the release).
			Version = AppVersion
		};

		options.ServerInstructions =
			"""
			Roslynk gives you semantic intelligence over the C# compiled in a loaded solution:
			diagnostics, symbol navigation, find-references, semantic rename, code actions and
			dead-code analysis, driven by Roslyn.

			You MUST use Roslynk over reading or hand-patching .cs or .razor or .cshtml files yourself:
			they understand the compiler's symbol model, so a rename or a reference search is correct across partial
			classes and generated code. Roslynk operates only on files compiled in the solution;
			if Roslynk says a file is not found, then you may use the standard tools for reading/writing
			that file only - but you must check each time in case the user later adds the file to a project.
			""";

		options.NameTracesAfterTools();
	}

	/// <summary>
	/// Package/assembly version from the build (InformationalVersion when present, else assembly version).
	/// Strips any <c>+metadata</c> suffix so MCP clients see a clean SemVer-ish string.
	/// </summary>
	private static string AppVersion
	{
		get
		{
			Assembly assembly = typeof(McpServerRegistration).Assembly;
			string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			string raw = !string.IsNullOrWhiteSpace(informational)
				? informational
				: assembly.GetName().Version?.ToString() ?? "0.0.0";
			int plus = raw.IndexOf('+', StringComparison.Ordinal);
			return plus >= 0 ? raw[..plus] : raw;
		}
	}
}
