using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Build.BuildSolution;

/// <summary>
/// The result of a full <c>dotnet build</c>: whether it succeeded, the error/warning counts from the
/// build summary, and up to the first 20 distinct error lines. The payload is null only when
/// <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.Indexing"/> because the solution is
/// still loading.
/// </summary>
public sealed record BuildSolutionResult : ResultBase
{
	public bool? Succeeded { get; init; }
	public int? Errors { get; init; }
	public int? Warnings { get; init; }
	public IReadOnlyList<string>? ErrorMessages { get; init; }
}
