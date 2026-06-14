using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>
/// Symbols whose name matched the query. A query that matches nothing is a success with an empty
/// <see cref="Results"/>, not a failure. <see cref="Truncated"/> is true when more matched than maxResults.
/// </summary>
public sealed class SearchSymbolsResult : ResultBase
{
	public IReadOnlyList<SymbolSearchResult>? Results { get; }
	public bool Truncated { get; }

	public SearchSymbolsResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, IReadOnlyList<SymbolSearchResult>? results, bool truncated)
		: base(solutionCurrentSnapshotId, status, error)
	{
		Results = results;
		Truncated = truncated;
	}
}
