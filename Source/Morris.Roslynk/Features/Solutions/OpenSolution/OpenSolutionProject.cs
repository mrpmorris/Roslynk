namespace Morris.Roslynk.Features.Solutions.OpenSolution;

/// <summary>A project within an opened solution. One entry per Roslyn project (so a multi-targeted
/// project appears once per target framework).</summary>
public sealed record OpenSolutionProject(string Name, int DocumentCount);
