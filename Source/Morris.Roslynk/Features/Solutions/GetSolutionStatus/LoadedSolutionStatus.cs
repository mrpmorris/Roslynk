using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>A currently-loaded solution: its handle, load status, current snapshot id, and project count.</summary>
public sealed class LoadedSolutionStatus
{
	public string SolutionId { get; }
	public SolutionStatus Status { get; }
	public string SnapshotId { get; }
	public int ProjectCount { get; }

	public LoadedSolutionStatus(string solutionId, SolutionStatus status, string snapshotId, int projectCount)
	{
		SolutionId = solutionId;
		Status = status;
		SnapshotId = snapshotId;
		ProjectCount = projectCount;
	}
}
