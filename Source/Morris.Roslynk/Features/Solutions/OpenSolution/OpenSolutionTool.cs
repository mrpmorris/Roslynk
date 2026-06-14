using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;

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
		Loads a C# solution (.sln or .slnx) into Roslynk so its projects and code can be queried.
		Idempotent: opening the same solution again returns the already-loaded instance.
		Returns the solution handle, its projects (with document counts), and any partial-load diagnostics.
		""")]
	public async Task<OpenSolutionResponse> OpenSolution(
		[Description("Absolute path to the .sln or .slnx file to open.")] string solutionPath)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionPath);

		OpenSolutionProject[] projects =
			instance.Workspace.Solution.Projects
				.Select(project => new OpenSolutionProject(project.Name, project.Documents.Count()))
				.ToArray();

		return new OpenSolutionResponse(
			SolutionId: instance.Key.Path,
			Projects: projects,
			LoadDiagnostics: instance.Workspace.LoadDiagnostics);
	}
}
