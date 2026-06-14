using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>A currently-loaded solution: its handle, load status, current snapshot id, and project count.</summary>
public sealed record LoadedSolutionStatus(string SolutionId, SolutionStatus Status, string SnapshotId, int ProjectCount);
