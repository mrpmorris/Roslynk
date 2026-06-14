using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.DeadCode.FindDeadCode;

/// <summary>
/// One symbol suspected to be unused: its fully-qualified name and kind, where it is declared, why it is
/// suspected, and a confidence. <c>High</c> = no references at all to a non-public member; <c>Medium</c> =
/// an unreferenced public member (it may be called externally) or one referenced only by test code.
/// </summary>
public sealed record DeadCodeCandidate(string Symbol, string Kind, string? SourcePath, string Reason, string Confidence);

/// <summary>
/// The suspected-dead symbols, most-confident first. <see cref="Truncated"/> is true when scanning stopped
/// at <c>maxResults</c> with candidates still unchecked. <see cref="Note"/> records the conservative caveats
/// that apply to every result so the host never reads a candidate as a bare "delete this". The payload is
/// null only when <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.Indexing"/> because the
/// solution is still loading.
/// </summary>
public sealed record FindDeadCodeResult : ResultBase
{
	public IReadOnlyList<DeadCodeCandidate>? Candidates { get; }
	public bool? Truncated { get; }
	public string? Note { get; }

	public FindDeadCodeResult(SolutionModel solutionModel, Error? error, IReadOnlyList<DeadCodeCandidate>? candidates, bool? truncated, string? note)
		: base(solutionModel, error)
	{
		Candidates = candidates;
		Truncated = truncated;
		Note = note;
	}
}
