namespace Morris.Roslynk.Features.Build.BuildSolution;

/// <summary>
/// The result of a full <c>dotnet build</c>: whether it succeeded, the error/warning counts from the
/// build summary, and up to the first 20 distinct error lines.
/// </summary>
public sealed record BuildSolutionResponse(
	bool Succeeded,
	int Errors,
	int Warnings,
	IReadOnlyList<string> ErrorMessages);
