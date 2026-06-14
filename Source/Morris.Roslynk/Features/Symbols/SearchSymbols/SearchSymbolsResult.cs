using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>
/// Symbols whose name matched the query. A query that matches nothing is a success with an empty
/// <see cref="Results"/>, not a failure. <see cref="Truncated"/> is true when more matched than maxResults.
/// </summary>
public sealed record SearchSymbolsResult : ResultBase
{
	public SearchSymbolsResult(SolutionModel solutionModel, Error? error, IReadOnlyList<SymbolSearchResult>? results, bool truncated)
		: base(solutionModel, error)
	{
		Results = results;
		Truncated = truncated;
	}

	public IReadOnlyList<SymbolSearchResult>? Results { get; }
	public bool Truncated { get; }
}
