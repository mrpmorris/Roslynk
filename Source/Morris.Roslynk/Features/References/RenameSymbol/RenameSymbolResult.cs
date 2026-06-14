using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.References.RenameSymbol;

/// <summary>
/// The outcome of a rename. On success <see cref="Applied"/> is true — or false for a checkOnly preview —
/// and <see cref="ChangedFiles"/> lists what was, or would be, rewritten. A refusal is carried as
/// <see cref="ResultBase.Error"/>: Invalid for a bad identifier, NotFound (with candidates) when nothing
/// matched, Ambiguous when several symbols share the name.
/// </summary>
public sealed record RenameSymbolResult : ResultBase
{
	public bool Applied { get; init; }
	public string? ResolvedSymbol { get; init; }
	public IReadOnlyList<string>? ChangedFiles { get; init; }
}
