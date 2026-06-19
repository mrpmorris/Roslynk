using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The outcome of a write transform run by the instance's single-writer consumer: the edited
/// <paramref name="Updated"/> snapshot to publish, and the solution-relative-or-absolute paths that were
/// changed on disk (empty for a watcher fold, which only reflects disk into memory and writes nothing).
/// </summary>
public sealed record WriteResult(Solution Updated, IReadOnlyList<string> ChangedPaths);