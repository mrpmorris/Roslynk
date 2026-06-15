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
	[Description(
		"""
		Lists the solutions currently loaded by the server, each with its load status and project progress:
		LoadedProjects is how many projects have loaded so far (a live count while still Building) and
		TotalProjects is the total once known (null until the first load finishes).
		""")]
	public GetSolutionStatusResponse GetSolutionStatus()
	{
		LoadedSolutionStatus[] solutions = InstanceRegistry.LoadedInstances()
			.Select(instance =>
			{
				SolutionModel model = instance.CurrentModel;
				int? totalProjects = model.Solution?.Projects.Count();
				int loadedProjects = model.Status == SolutionStatus.Ready && totalProjects is int total
					? total
					: instance.LoadedProjects;
				return new LoadedSolutionStatus(instance.Key.FilePath, model.Status, model.SnapshotId, loadedProjects, totalProjects);
			})
			.ToArray();

		return new GetSolutionStatusResponse(solutions);
	}
}
