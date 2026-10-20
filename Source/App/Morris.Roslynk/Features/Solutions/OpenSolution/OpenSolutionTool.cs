using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
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
		Opens a .sln/.slnx and waits for the initial load (up to the server's load timeout); repeated opens reuse the instance.
		Headers: solutionId, status, projects, loadDiagnostics. Body: <projectPath>,<documentCount> per project.
		noWait=true starts or reuses the load and returns immediately with status=Building (fire-and-forget pre-warming).
		Missing solution: NotFound; failed load: Faulted.
		""")]
	[McpServerTool(
		Name = OpenSolutionName,
		Title = "Open a C# solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> OpenSolution(
		[Description(ParameterDescriptions.SolutionPath)] string solutionPath,
		[Description("Return immediately without waiting for the initial load.")] bool noWait = false,
		CancellationToken cancellationToken = default)
	{
		string fullPath = Path.GetFullPath(solutionPath);
		if (!File.Exists(fullPath))
			return OutlineError.Format(
				Error.NotFound($"No solution file was found at '{fullPath}'."),
				SolutionStatus.Faulted);

		RoslynInstance instance = noWait
			? InstanceRegistry.GetOrBegin(solutionPath)
			: await InstanceRegistry.GetOrBeginAsync(solutionPath, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

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
