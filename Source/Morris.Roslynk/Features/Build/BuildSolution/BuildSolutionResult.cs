using Morris.Roslynk.Infrastructure.Lifecycle;
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
	public BuildSolutionResult(SolutionModel solutionModel, Error? error, bool? succeeded, int? errors, int? warnings, IReadOnlyList<string>? errorMessages)
		: base(solutionModel, error)
	{
		Succeeded = succeeded;
		Errors = errors;
		Warnings = warnings;
		ErrorMessages = errorMessages;
	}

	public bool? Succeeded { get; }
	public int? Errors { get; }
	public int? Warnings { get; }
	public IReadOnlyList<string>? ErrorMessages { get; }
}
