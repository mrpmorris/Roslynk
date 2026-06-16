using System.ComponentModel;
using ModelContextProtocol.Server;
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

	[McpServerTool(
		Name = ReloadSolutionName,
		Title = "Reload a solution",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Reloads a solution from disk in the background; the cleanup after a project or build-file change the
		incremental model cannot absorb. Returns a header-only text result, not JSON: '#solutionId', '#status',
		'#projects', '#snapshot'. Returns immediately; the previous snapshot keeps serving reads (status
		Building) until the fresh one is ready. No effect on files. A failed reload is returned as a Faulted
		#error.
		""")]
	public string ReloadSolution(
		[Description("Solution handle returned by open_solution.")] string solutionId)
	{
		RoslynInstance instance = InstanceRegistry.BeginReload(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Status == SolutionStatus.Faulted)
			return OutlineError.Format(Error.Faulted(model.FaultMessage ?? "The reload failed."), model.Status, model.SnapshotId);

		return new OutlineBuilder()
			.Header("solutionId", instance.Key.FilePath)
			.Status(model.Status)
			.Header("projects", model.Solution?.Projects.Count() ?? 0)
			.Snapshot(model.SnapshotId)
			.ToString();
	}
}
