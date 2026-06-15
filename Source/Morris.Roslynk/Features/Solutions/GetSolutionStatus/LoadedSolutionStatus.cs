using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>
/// A currently-loaded solution: its handle, load status, current snapshot id, and load progress.
/// <see cref="LoadedProjects"/> is how many projects have loaded so far — a live count while the solution
/// is <see cref="SolutionStatus.Building"/>; <see cref="TotalProjects"/> is the total once known, and is
/// null until the first load finishes (during the initial load the total is not yet known).
/// </summary>
public sealed class LoadedSolutionStatus
{
	public string SolutionId { get; }
	public SolutionStatus Status { get; }
	public string SnapshotId { get; }
	public int LoadedProjects { get; }
	public int? TotalProjects { get; }

	public LoadedSolutionStatus(string solutionId, SolutionStatus status, string snapshotId, int loadedProjects, int? totalProjects)
	{
		SolutionId = solutionId;
		Status = status;
		SnapshotId = snapshotId;
		LoadedProjects = loadedProjects;
		TotalProjects = totalProjects;
	}
}
