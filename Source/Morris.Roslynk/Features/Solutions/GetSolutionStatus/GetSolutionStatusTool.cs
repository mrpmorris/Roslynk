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
			.Select(instance => new LoadedSolutionStatus(instance.Key.Path, instance.CurrentSolution.Projects.Count()))
			.ToArray();

		return new GetSolutionStatusResponse(solutions);
	}
}
