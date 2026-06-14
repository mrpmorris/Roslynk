namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>The solutions currently loaded by the server.</summary>
public sealed record GetSolutionStatusResponse(IReadOnlyList<LoadedSolutionStatus> Solutions);
