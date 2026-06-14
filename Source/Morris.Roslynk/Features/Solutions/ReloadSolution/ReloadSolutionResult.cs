using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Solutions.ReloadSolution;

/// <summary>
/// The result of reloading a solution. reload_solution does not block: the current snapshot keeps being
/// served as <see cref="SolutionStatus.Building"/> while a fresh one loads in the background, so
/// <see cref="ProjectCount"/> reflects whatever snapshot is currently available. A load failure is carried
/// as a Faulted <see cref="ResultBase.Error"/>.
/// </summary>
public sealed class ReloadSolutionResult : ResultBase
{
	public string? SolutionId { get; }
	public int ProjectCount { get; }

	public ReloadSolutionResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, string? solutionId, int projectCount)
		: base(solutionCurrentSnapshotId, status, error)
	{
		SolutionId = solutionId;
		ProjectCount = projectCount;
	}
}
