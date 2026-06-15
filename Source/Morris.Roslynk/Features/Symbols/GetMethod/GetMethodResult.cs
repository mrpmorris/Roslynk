using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// The methods matching a name; one entry per overload. When the name does not resolve to a method,
/// <see cref="ResultBase.Error"/> carries a <see cref="ErrorCode.NotFound"/> whose candidates list any
/// non-method symbols that did resolve, or fuzzy near-misses when nothing matched at all.
/// </summary>
public sealed class GetMethodResult : ResultBase
{
	public IReadOnlyList<MethodDto>? Methods { get; }

	public GetMethodResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error, IReadOnlyList<MethodDto>? methods)
		: base(solutionCurrentSnapshotId, status, error)
	{
		Methods = methods;
	}
}
