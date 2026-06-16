using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.CloseSolution;

[McpServerToolType]
public sealed class CloseSolutionTool
{
	public const string CloseSolutionName = "close_solution";

	private readonly InstanceRegistry InstanceRegistry;

	public CloseSolutionTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = CloseSolutionName,
		Title = "Close a solution",
		ReadOnly = false,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description("Unloads a solution, freeing its workspace. No effect on files. Returns an empty result.")]
	public string CloseSolution(
		[Description("Solution handle returned by open_solution.")] string solutionId)
	{
		InstanceRegistry.TryClose(solutionId);
		return "";
	}
}
