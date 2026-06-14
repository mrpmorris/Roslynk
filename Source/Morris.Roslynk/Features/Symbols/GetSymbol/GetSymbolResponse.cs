namespace Morris.Roslynk.Features.Symbols.GetSymbol;

/// <summary>
/// The resolved symbol's details, or — when the name resolves to more than one symbol — the candidate
/// fully-qualified names to disambiguate with (and a null <c>Symbol</c>).
/// </summary>
public sealed record GetSymbolResponse(
	SymbolDto? Symbol,
	IReadOnlyList<string> Candidates);
