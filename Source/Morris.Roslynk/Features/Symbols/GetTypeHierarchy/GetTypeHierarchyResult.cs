using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetTypeHierarchy;

/// <summary>
/// A type's base chain, implemented interfaces, and known derived types (all fully-qualified). When the
/// name does not resolve to a single type, <see cref="ResultBase.Error"/> carries a
/// <see cref="ErrorCode.NotFound"/> (nothing matched) or <see cref="ErrorCode.Ambiguous"/> (several
/// matched) whose candidates list the matching fully-qualified names.
/// </summary>
public sealed record GetTypeHierarchyResult : ResultBase
{
	public string? ResolvedType { get; init; }
	public IReadOnlyList<string>? BaseTypes { get; init; }
	public IReadOnlyList<string>? Interfaces { get; init; }
	public IReadOnlyList<string>? DerivedTypes { get; init; }
}
