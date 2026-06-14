namespace Morris.Roslynk.Features.Symbols.GetMembers;

/// <summary>
/// A type's members. When the name is ambiguous or not a type, <c>ResolvedType</c> is null and
/// <c>Candidates</c> lists any matches.
/// </summary>
public sealed record GetMembersResponse(
	string? ResolvedType,
	IReadOnlyList<MemberDto> Members,
	IReadOnlyList<string> Candidates);
