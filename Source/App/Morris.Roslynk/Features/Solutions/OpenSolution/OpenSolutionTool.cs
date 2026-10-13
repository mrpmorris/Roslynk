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

	[Description("""
		Opens a .sln/.slnx asynchronously; repeated opens reuse the instance.
		Headers: solutionId, status, projects, loadDiagnostics. Body: <projectPath>,<documentCount> per project.
		While loading: status=Building, empty body. Missing solution: NotFound; failed load: Faulted.
		""")]
	[McpServerTool(
		Name = OpenSolutionName,
		Title = "Open a C# solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public string OpenSolution(
		[Description("Absolute path to the .sln or .slnx file to open.")] string solutionPath)
	{
		string fullPath = Path.GetFullPath(solutionPath);
		if (!File.Exists(fullPath))
			return OutlineError.Format(
				Error.NotFound($"No solution file was found at '{fullPath}'."),
				SolutionStatus.Faulted);

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
