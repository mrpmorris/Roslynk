namespace Morris.Roslynk.Features.Callers.GetCallers;

/// <summary>
/// The symbols that call the resolved method (as fully-qualified names). When the name is ambiguous,
/// <c>Callers</c> is empty and <c>Candidates</c> lists the matches.
/// </summary>
public sealed record GetCallersResponse(
	string? ResolvedSymbol,
	IReadOnlyList<string> Callers,
	IReadOnlyList<string> Candidates);
