using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetTypeHierarchy;

/// <summary>
/// A type's base chain, implemented interfaces, and known derived types (all fully-qualified). When the
/// name does not resolve to a single type, <see cref="ResultBase.Error"/> carries a
/// <see cref="ErrorCode.NotFound"/> (nothing matched) or <see cref="ErrorCode.Ambiguous"/> (several
/// matched) whose candidates list the matching fully-qualified names.
/// </summary>
public sealed class GetTypeHierarchyResult : ResultBase
{
	public string? ResolvedType { get; }
	public IReadOnlyList<string>? BaseTypes { get; }
	public IReadOnlyList<string>? Interfaces { get; }
	public IReadOnlyList<string>? DerivedTypes { get; }

	public GetTypeHierarchyResult(
		string solutionCurrentSnapshotId,
		SolutionStatus status,
		Error? error,
		string? resolvedType,
		IReadOnlyList<string>? baseTypes,
		IReadOnlyList<string>? interfaces,
		IReadOnlyList<string>? derivedTypes)
		: base(solutionCurrentSnapshotId, status, error)
	{
		ResolvedType = resolvedType;
		BaseTypes = baseTypes;
		Interfaces = interfaces;
		DerivedTypes = derivedTypes;
	}
}
