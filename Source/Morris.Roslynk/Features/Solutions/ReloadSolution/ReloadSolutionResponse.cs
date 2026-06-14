namespace Morris.Roslynk.Features.Solutions.ReloadSolution;

/// <summary>The reloaded solution's handle and project count.</summary>
public sealed record ReloadSolutionResponse(string SolutionId, int ProjectCount);
