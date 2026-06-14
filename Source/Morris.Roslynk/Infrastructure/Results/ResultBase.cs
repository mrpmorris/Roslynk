using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// The envelope every tool result derives from: the <see cref="SolutionCurrentSnapshotId"/> of the
/// snapshot the answer was computed against, the model's <see cref="Status"/> at that point, and a
/// structured <see cref="Error"/> that is present when the request did not succeed (null when it did).
/// The properties are get-only; each feature's payload lives on the derived type beside its tool.
/// </summary>
public abstract class ResultBase
{
	public string SolutionCurrentSnapshotId { get; }
	public SolutionStatus Status { get; }
	public Error? Error { get; }

	public bool IsSuccess => Error is null;

	protected ResultBase(string solutionCurrentSnapshotId, SolutionStatus status, Error? error)
	{
		SolutionCurrentSnapshotId = solutionCurrentSnapshotId ?? throw new ArgumentNullException(nameof(solutionCurrentSnapshotId));
		Status = status;
		Error = error;
	}
}
