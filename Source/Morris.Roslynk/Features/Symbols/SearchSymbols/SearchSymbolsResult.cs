using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>
/// Symbols whose name matched the query. A query that matches nothing is a success with an empty
/// <see cref="Results"/>, not a failure. <see cref="Truncated"/> is true when more matched than maxResults.
/// </summary>
public sealed record SearchSymbolsResult : ResultBase
{
	public IReadOnlyList<SymbolSearchResult>? Results { get; init; }
	public bool Truncated { get; init; }
}
