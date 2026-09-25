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
			Version = "1.0.0"
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

			To fix a diagnostic, call apply_code_fix with its id, line and column; never hand-edit or apply_patch
			to make a diagnostic go away. If it returns error=Conflict with several candidates, show the user the
			candidate titles and ask which to apply, unless their request clearly names one, then pass that actionId
			to apply_code_action. A fix must keep the code's declared intent: don't remove an interface, base type,
			member, attribute or other declaration to silence an error unless the user asks for that.
			""";

		options.NameTracesAfterTools();
	}
}
