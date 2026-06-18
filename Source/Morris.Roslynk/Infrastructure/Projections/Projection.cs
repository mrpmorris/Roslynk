using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Projections;

/// <summary>
/// One view of the loaded solution under a specific preprocessor-symbol set. The base projection is the
/// solution as MSBuild loaded it; derived projections flip a conditionally-compiled symbol so that
/// <c>#if</c> branches that are inactive in the loaded configuration become analyzable. Running a semantic
/// query against every projection and merging the results is how Roslynk covers all branches.
/// </summary>
/// <param name="Label">A short tag for the projection, e.g. <c>base</c>, <c>!DEBUG</c>, or <c>FEATURE_X</c>.</param>
/// <param name="Solution">The Roslyn solution for this projection.</param>
public sealed record Projection(string Label, Solution Solution);

/// <summary>A symbol resolved within a particular <see cref="Projection"/>; the projection is carried so a
/// follow-up query (find-references, etc.) runs against the solution the symbol actually belongs to.</summary>
public sealed record ProjectionSymbol(Projection Projection, ISymbol Symbol);
