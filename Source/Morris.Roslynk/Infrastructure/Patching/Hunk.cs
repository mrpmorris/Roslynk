namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// A single hunk from a unified diff: the 1-based start lines and lengths from its <c>@@</c> header and
/// the ordered body lines. The lengths default to 1 when omitted (the unified-diff convention).
/// </summary>
public sealed record Hunk(int OldStart, int OldLength, int NewStart, int NewLength, IReadOnlyList<HunkLine> Lines);
