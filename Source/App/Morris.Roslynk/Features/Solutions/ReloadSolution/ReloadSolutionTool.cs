using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

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

	[Description("""
		Re-evaluates an open solution from disk using MSBuild. Additional reload reasons: missed watcher
		events (network/WSL/container filesystems), global.json changes or retrying a Faulted load.

		Returns immediately: solutionId (absolute path with forward slashes), status, projects. Previous snapshot serves reads with
		status=Building until replacement is ready. Files are unchanged; failed reloads return Faulted.
		""")]
	[McpServerTool(
		Name = ReloadSolutionName,
		Title = "Reload a solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public string ReloadSolution(
		[Description(ParameterDescriptions.SolutionId)] string solutionId)
	{
		RoslynInstance instance = InstanceRegistry.BeginReload(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Status == SolutionStatus.Faulted)
			return OutlineError.Format(Error.Faulted(model.FaultMessage ?? "The reload failed."), model.Status);

		return new OutlineBuilder()
			.Header("solutionId", instance.Key.SolutionId)
			.Status(model.Status)
			.Header("projects", model.Solution?.Projects.Count() ?? 0)
			.ToString();
	}
}
