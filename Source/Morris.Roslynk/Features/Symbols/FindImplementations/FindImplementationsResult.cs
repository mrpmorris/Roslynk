using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.FindImplementations;

/// <summary>
/// The implementations / overrides of the resolved interface or abstract member (as fully-qualified
/// names), with the resolved symbol's own fully-qualified name. When the name does not resolve,
/// <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.NotFound"/>; when it is ambiguous it
/// carries an <see cref="ErrorCode.Ambiguous"/> whose candidates list the matches. A symbol that resolves
/// but has no implementations is a success with an empty list.
/// </summary>
public sealed record FindImplementationsResult : ResultBase
{
	public string? ResolvedSymbol { get; init; }
	public IReadOnlyList<string>? Implementations { get; init; }
}
