using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// The envelope every tool result derives from: the <see cref="SnapshotId"/> of the solution snapshot
/// that produced it, the model's <see cref="Status"/> at that point (so a caller can tell a fresh answer
/// from one served while a rebuild is in flight), and a structured <see cref="Error"/> when the request
/// did not succeed (null when it did). Each feature's payload lives on the derived type beside its tool.
/// </summary>
public abstract record ResultBase
{
	public required string SnapshotId { get; init; }
	public required SolutionStatus Status { get; init; }
	public Error? Error { get; init; }

	public bool IsSuccess => Error is null;
}
