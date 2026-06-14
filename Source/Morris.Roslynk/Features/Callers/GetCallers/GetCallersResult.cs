using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Callers.GetCallers;

/// <summary>
/// The symbols that call the resolved method (as fully-qualified names). A method with no callers is a
/// success with an empty <see cref="Callers"/>. When the name does not resolve to a single symbol,
/// <see cref="ResultBase.Error"/> carries a <see cref="ErrorCode.NotFound"/> (nothing matched) or
/// <see cref="ErrorCode.Ambiguous"/> (several matched) whose candidates list the matching names.
/// </summary>
public sealed class GetCallersResult : ResultBase
{
	public string? ResolvedSymbol { get; }
	public IReadOnlyList<string>? Callers { get; }

	public GetCallersResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, string? resolvedSymbol, IReadOnlyList<string>? callers)
		: base(solutionCurrentSnapshotId, status, error)
	{
		ResolvedSymbol = resolvedSymbol;
		Callers = callers;
	}
}
