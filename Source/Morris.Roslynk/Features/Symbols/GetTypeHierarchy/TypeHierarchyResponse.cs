namespace Morris.Roslynk.Features.Symbols.GetTypeHierarchy;

/// <summary>
/// A type's base chain, implemented interfaces, and known derived types (all fully-qualified). When the
/// name is ambiguous or not a type, <c>ResolvedType</c> is null and <c>Candidates</c> lists any matches.
/// </summary>
public sealed record TypeHierarchyResponse(
	string? ResolvedType,
	IReadOnlyList<string> BaseTypes,
	IReadOnlyList<string> Interfaces,
	IReadOnlyList<string> DerivedTypes,
	IReadOnlyList<string> Candidates);
