using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.ReloadSolution;

[McpServerToolType]
public sealed class ReloadSolutionTool
{
	public const string ReloadSolutionName = "reload_solution";

	private readonly InstanceRegistry InstanceRegistry;

	public ReloadSolutionTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = ReloadSolutionName,
		Title = "Reload a solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description("Discards the in-memory workspace for a solution and loads it fresh from disk. No effect on files.")]
	public async Task<ReloadSolutionResponse> ReloadSolution(
		[Description("Solution handle returned by open_solution.")] string solutionId)
	{
		RoslynInstance instance = await InstanceRegistry.ReloadAsync(solutionId);
		return new ReloadSolutionResponse(instance.Key.Path, instance.CurrentSolution.Projects.Count());
	}
}
