using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Solutions.OpenSolution;

/// <summary>
/// The result of opening a solution. open_solution does not block: it returns immediately with the
/// solution's <see cref="ResultBase.Status"/> — <see cref="SolutionStatus.Building"/> while it loads in
/// the background, then <see cref="SolutionStatus.Ready"/>. <see cref="Projects"/> is populated once a
/// snapshot is available; poll get_solution_status (or call open_solution again) until it is Ready. A
/// load failure is carried as a Faulted <see cref="ResultBase.Error"/>.
/// </summary>
public sealed record OpenSolutionResult : ResultBase
{
	public string? SolutionId { get; init; }
	public IReadOnlyList<OpenSolutionProject>? Projects { get; init; }
	public IReadOnlyList<string>? LoadDiagnostics { get; init; }
}
