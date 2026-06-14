namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>Matching symbols. <c>Truncated</c> is true when more matched than <c>maxResults</c>.</summary>
public sealed record SearchSymbolsResponse(IReadOnlyList<SymbolSearchResult> Results, bool Truncated);
