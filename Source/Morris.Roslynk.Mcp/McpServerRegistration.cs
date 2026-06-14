using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
			Title = "Roslynk — C# semantic intelligence",
			Version = "1.0.0"
		};

		options.ServerInstructions =
			"""
			Roslynk gives you semantic intelligence over the C# compiled in a loaded solution:
			diagnostics, symbol navigation, find-references, semantic rename, code actions and
			dead-code analysis, driven by Roslyn.

			Prefer these tools over reading or hand-patching .cs yourself: they understand the
			compiler's symbol model, so a rename or a reference search is correct across partial
			classes and generated code. Roslynk operates only on .cs compiled in the solution;
			anything else is the host's job.
			""";
	}
}
