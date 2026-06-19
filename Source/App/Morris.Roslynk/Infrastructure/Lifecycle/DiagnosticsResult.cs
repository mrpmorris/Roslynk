using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The result of a deferred diagnostics build: the computed <paramref name="Diagnostics"/> together with the
/// exact <paramref name="Solution"/> snapshot they were compiled from, so the caller formats locations
/// against the same trees they were produced from (a write could otherwise swap the snapshot underneath).
/// </summary>
public sealed record DiagnosticsResult(IReadOnlyList<Diagnostic> Diagnostics, Solution Solution);