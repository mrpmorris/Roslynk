namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>A symbol matched by a search, by fully-qualified name and kind.</summary>
public sealed record SymbolSearchResult(string FullName, string Kind);
