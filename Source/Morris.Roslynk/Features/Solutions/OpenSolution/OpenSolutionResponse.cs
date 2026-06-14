namespace Morris.Roslynk.Features.Solutions.OpenSolution;

/// <summary>
/// The result of opening a solution: its handle, the projects it contains, and any partial-load
/// diagnostics MSBuild reported.
/// </summary>
public sealed record OpenSolutionResponse(
	string SolutionId,
	IReadOnlyList<OpenSolutionProject> Projects,
	IReadOnlyList<string> LoadDiagnostics);
