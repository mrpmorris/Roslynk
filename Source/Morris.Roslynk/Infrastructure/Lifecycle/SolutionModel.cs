using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// An immutable snapshot of a loaded solution together with its <see cref="Status"/> and an opaque,
/// monotonically-increasing <see cref="SnapshotId"/>. Instances are swapped atomically on
/// <see cref="RoslynInstance"/>, so a reader always sees a consistent (solution, status) pair without
/// locking. <see cref="Solution"/> is null only before the very first load completes.
/// </summary>
public sealed record SolutionModel
{
	public required string SnapshotId { get; init; }
	public required SolutionStatus Status { get; init; }
	public Solution? Solution { get; init; }
	public string? FaultMessage { get; init; }

	/// <summary>A load or rebuild in flight, optionally still serving the previous <paramref name="solution"/>.</summary>
	public static SolutionModel Loading(string snapshotId, Solution? solution) =>
		new() { SnapshotId = snapshotId, Status = SolutionStatus.Building, Solution = solution };

	/// <summary>A published snapshot ready to be read.</summary>
	public static SolutionModel Ready(string snapshotId, Solution solution) =>
		new() { SnapshotId = snapshotId, Status = SolutionStatus.Ready, Solution = solution };

	/// <summary>A load that failed; <paramref name="faultMessage"/> explains why and no snapshot is served.</summary>
	public static SolutionModel Faulted(string snapshotId, string faultMessage) =>
		new() { SnapshotId = snapshotId, Status = SolutionStatus.Faulted, FaultMessage = faultMessage };
}
