using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.References.FindReferences;

/// <summary>
/// References to a resolved symbol. A symbol with no references is a success with an empty
/// <see cref="References"/>; <see cref="Truncated"/> is true when more matched than maxResults. When the
/// name does not resolve to a single symbol, <see cref="ResultBase.Error"/> carries a
/// <see cref="ErrorCode.NotFound"/> (whose candidates are ranked near-miss suggestions) or
/// <see cref="ErrorCode.Ambiguous"/> (whose candidates are the matching display names).
/// </summary>
public sealed class FindReferencesResult : ResultBase
{
	public string? ResolvedSymbol { get; }
	public IReadOnlyList<ReferenceDto>? References { get; }
	public bool Truncated { get; }

	public FindReferencesResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, string? resolvedSymbol, IReadOnlyList<ReferenceDto>? references, bool truncated)
		: base(solutionCurrentSnapshotId, status, error)
	{
		ResolvedSymbol = resolvedSymbol;
		References = references;
		Truncated = truncated;
	}
}
