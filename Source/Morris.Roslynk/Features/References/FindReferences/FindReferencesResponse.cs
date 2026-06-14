namespace Morris.Roslynk.Features.References.FindReferences;

/// <summary>
/// References to a resolved symbol. When the name resolves to more than one symbol, <c>References</c>
/// is empty and <c>Candidates</c> lists the matching fully-qualified names to disambiguate with.
/// </summary>
public sealed record FindReferencesResponse(
	string? ResolvedSymbol,
	IReadOnlyList<ReferenceDto> References,
	IReadOnlyList<string> Candidates);
