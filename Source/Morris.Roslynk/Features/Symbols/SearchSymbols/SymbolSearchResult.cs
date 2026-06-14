namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

/// <summary>A symbol matched by a search, by fully-qualified name and kind.</summary>
public sealed class SymbolSearchResult
{
	public string FullName { get; }
	public string Kind { get; }

	public SymbolSearchResult(string fullName, string kind)
	{
		FullName = fullName;
		Kind = kind;
	}
}
