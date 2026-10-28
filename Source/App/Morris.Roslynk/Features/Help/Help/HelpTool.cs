using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk.Features.Help.Help;

[McpServerToolType]
public sealed class HelpTool : IMcpTool
{
	public const string HelpName = "help";

	public static string GetName() => HelpName;

	public static string GetDescription() => "Returns the full usage description for a tool by tool name.";


	[McpServerTool(
		Name = HelpName,
		Title = "Get tool help",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public string Help([Description("MCP tool name, such as get_members.")] string toolName)
	{
		if (McpToolCatalog.Descriptions.TryGetValue(toolName, out string? description))
			return description;

		return OutlineError.Format(
			Error.NotFound($"No MCP tool named '{toolName}'.", McpToolCatalog.Descriptions.Keys.Order(StringComparer.Ordinal).ToArray()),
			SolutionStatus.Ready);
	}
}
