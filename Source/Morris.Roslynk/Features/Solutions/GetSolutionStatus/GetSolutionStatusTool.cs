using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

[McpServerToolType]
public sealed class GetSolutionStatusTool
{
	public const string GetSolutionStatusName = "get_solution_status";

	private readonly InstanceRegistry InstanceRegistry;

	public GetSolutionStatusTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = GetSolutionStatusName,
		Title = "Get loaded-solution status",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description("Lists the solutions currently loaded by the server, with their project counts.")]
	public GetSolutionStatusResponse GetSolutionStatus()
	{
		LoadedSolutionStatus[] solutions = InstanceRegistry.LoadedInstances()
			.Select(instance =>
			{
				SolutionModel model = instance.CurrentModel;
				return new LoadedSolutionStatus(instance.Key.FilePath, model.Status, model.SnapshotId, model.Solution?.Projects.Count() ?? 0);
			})
			.ToArray();

		return new GetSolutionStatusResponse(solutions);
	}
}
