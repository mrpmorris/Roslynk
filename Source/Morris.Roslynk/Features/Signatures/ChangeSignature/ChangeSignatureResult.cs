using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Signatures.ChangeSignature;

/// <summary>
/// The outcome of a change-signature. On success <see cref="Applied"/> is true — or false for a checkOnly
/// preview — <see cref="ChangedFiles"/> lists what was rewritten and <see cref="UpdatedCallSites"/> how
/// many invocations gained the new argument. A refusal is carried on <see cref="ResultBase.Error"/>:
/// Invalid for bad input, NotFound/Ambiguous for resolution, NotSupported for an unsupported method shape
/// (with the resolved name still in <see cref="ResolvedMethod"/>).
/// </summary>
public sealed class ChangeSignatureResult : ResultBase
{
	public bool Applied { get; }
	public string? ResolvedMethod { get; }
	public IReadOnlyList<string>? ChangedFiles { get; }
	public int UpdatedCallSites { get; }

	public ChangeSignatureResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, bool applied, string? resolvedMethod, IReadOnlyList<string>? changedFiles, int updatedCallSites)
		: base(solutionCurrentSnapshotId, status, error)
	{
		Applied = applied;
		ResolvedMethod = resolvedMethod;
		ChangedFiles = changedFiles;
		UpdatedCallSites = updatedCallSites;
	}
}
