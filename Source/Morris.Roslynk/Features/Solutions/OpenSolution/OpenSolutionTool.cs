using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Solutions.OpenSolution;

[McpServerToolType]
public sealed class OpenSolutionTool
{
	public const string OpenSolutionName = "open_solution";

	private readonly InstanceRegistry InstanceRegistry;

	public OpenSolutionTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = OpenSolutionName,
		Title = "Open a C# solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Loads a C# solution (.sln or .slnx) into Roslynk so its projects and code can be queried. Returns
		immediately: the solution loads in the background, so the result's status is Building until it is
		Ready — poll get_solution_status, or call open_solution again, for the projects. Idempotent: opening
		the same solution again returns the same instance.
		""")]
	public OpenSolutionResult OpenSolution(
		[Description("Absolute path to the .sln or .slnx file to open.")] string solutionPath)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionPath);
		SolutionModel model = instance.CurrentModel;

		OpenSolutionProject[] projects = model.Solution is null
			? []
			: model.Solution.Projects
				.Select(project => new OpenSolutionProject(project.Name, project.Documents.Count()))
				.ToArray();

		return new OpenSolutionResult(
			model.SnapshotId,
			model.Status,
			model.Status == SolutionStatus.Faulted
				? Error.Faulted(model.FaultMessage ?? "The solution failed to load.")
				: null,
			solutionId: instance.Key.Path,
			projects: projects,
			loadDiagnostics: instance.Workspace?.LoadDiagnostics ?? []);
	}
}
