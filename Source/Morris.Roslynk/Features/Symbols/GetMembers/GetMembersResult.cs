using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetMembers;

/// <summary>
/// A type's members, with the type's resolved fully-qualified name. When the name does not resolve to a
/// type, <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.NotFound"/>; when it is ambiguous
/// it carries an <see cref="ErrorCode.Ambiguous"/> whose candidates list the matches.
/// </summary>
public sealed record GetMembersResult : ResultBase
{
	public string? ResolvedType { get; }
	public IReadOnlyList<MemberDto>? Members { get; }

	public GetMembersResult(SolutionModel solutionModel, Error? error, string? resolvedType, IReadOnlyList<MemberDto>? members)
		: base(solutionModel, error)
	{
		ResolvedType = resolvedType;
		Members = members;
	}
}
