using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

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
		$"""
		Loads a C# solution (.sln or .slnx) into Roslynk so its projects and code can be queried. Returns a
		compact text result, not JSON: a '#solutionId', '#status', '#projects', '#loadDiagnostics'
		header, a blank line, then one '<projectPath>,<documentCount>' line per project. Returns immediately:
		the solution loads in the background, so status is Building (and the body empty) until it is Ready; poll
		get_solution_status every 1 second and report project-loading progress, or call open_solution again.
		Idempotent: opening the same solution again returns the same instance. A load failure is returned as a
		Faulted #error. {OutlineDescriptions.Freshness}
		""")]
	public string OpenSolution(
		[Description("Absolute path to the .sln or .slnx file to open.")] string solutionPath)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionPath);
		SolutionModel model = instance.CurrentModel;

		if (model.Status == SolutionStatus.Faulted)
			return OutlineError.Format(Error.Faulted(model.FaultMessage ?? "The solution failed to load."), model.Status);

		string? solutionDirectory = model.Solution is null
			? Path.GetDirectoryName(instance.Key.FilePath)
			: SolutionRelativePath.DirectoryOf(model.Solution);
		int loadDiagnostics = instance.Workspace?.LoadDiagnostics.Count ?? 0;
		List<Project> projects = model.Solution?.Projects.ToList() ?? [];

		var builder = new OutlineBuilder();
		builder.Header("solutionId", instance.Key.FilePath);
		builder.Status(model.Status);
		builder.Header("projects", projects.Count);
		builder.Header("loadDiagnostics", loadDiagnostics);
		builder.BeginBody();

		foreach (Project project in projects)
		{
			string path = SolutionRelativePath.Of(solutionDirectory, project.FilePath) ?? project.Name;
			builder.Line(0, $"{path},{project.Documents.Count()}");
		}

		return builder.ToString();
	}
}
