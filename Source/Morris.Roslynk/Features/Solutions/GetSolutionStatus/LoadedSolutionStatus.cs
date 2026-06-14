namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>A currently-loaded solution and its project count.</summary>
public sealed record LoadedSolutionStatus(string SolutionId, int ProjectCount);
