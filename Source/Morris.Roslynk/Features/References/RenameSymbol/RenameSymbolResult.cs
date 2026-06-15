using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.References.RenameSymbol;

/// <summary>
/// The outcome of a rename. On success <see cref="Applied"/> is true; or false for a checkOnly preview —
/// and <see cref="ChangedFiles"/> lists what was, or would be, rewritten. A refusal is carried on
/// <see cref="ResultBase.Error"/>: Invalid for a bad identifier, NotFound (with candidates) when nothing
/// matched, Ambiguous when several symbols share the name.
/// </summary>
public sealed class RenameSymbolResult : ResultBase
{
	public bool Applied { get; }
	public string? ResolvedSymbol { get; }
	public IReadOnlyList<string>? ChangedFiles { get; }

	public RenameSymbolResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, bool applied, string? resolvedSymbol, IReadOnlyList<string>? changedFiles)
		: base(solutionCurrentSnapshotId, status, error)
	{
		Applied = applied;
		ResolvedSymbol = resolvedSymbol;
		ChangedFiles = changedFiles;
	}
}
