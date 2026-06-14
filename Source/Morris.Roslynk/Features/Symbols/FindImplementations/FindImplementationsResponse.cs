namespace Morris.Roslynk.Features.Symbols.FindImplementations;

/// <summary>
/// Implementations / overrides of the resolved interface or abstract member (as fully-qualified names).
/// When the name is ambiguous, <c>Implementations</c> is empty and <c>Candidates</c> lists the matches.
/// </summary>
public sealed record FindImplementationsResponse(
	string? ResolvedSymbol,
	IReadOnlyList<string> Implementations,
	IReadOnlyList<string> Candidates);
