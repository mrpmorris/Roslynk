using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// The envelope every tool result derives from. It is constructed from the <see cref="SolutionModel"/>
/// the answer was computed against — so <see cref="SolutionCurrentSnapshotId"/> and <see cref="Status"/>
/// are always consistent and a derived result cannot be built without them — plus a structured
/// <see cref="Error"/> that is present when the request did not succeed (null when it did). The properties
/// are get-only; each feature's payload lives on the derived type beside its tool.
/// </summary>
public abstract record ResultBase
{
	public string SolutionCurrentSnapshotId { get; }
	public SolutionStatus Status { get; }
	public Error? Error { get; }

	public bool IsSuccess => Error is null;

	protected ResultBase(SolutionModel solutionModel, Error? error)
	{
		if (solutionModel is null)
			throw new ArgumentNullException(nameof(solutionModel));

		SolutionCurrentSnapshotId = solutionModel.SnapshotId;
		Status = solutionModel.Status;
		Error = error;
	}
}
