using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Mcp.Observability;

namespace Morris.Roslynk.Mcp;

/// <summary>
/// Server identity for the whole Roslynk tool surface. Tool guidance lives in the roslynk skill
/// (skills/roslynk/SKILL.md plus one reference file per tool), not in MCP metadata.
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

		options.NameTracesAfterTools();
	}
}
