namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// A single hunk from a unified diff: the 1-based start lines and lengths from its <c>@@</c> header and
/// the ordered body lines. The lengths default to 1 when omitted (the unified-diff convention).
/// </summary>
public sealed class Hunk
{
	public int OldStart { get; }
	public int OldLength { get; }
	public int NewStart { get; }
	public int NewLength { get; }
	public IReadOnlyList<HunkLine> Lines { get; }

	public Hunk(int oldStart, int oldLength, int newStart, int newLength, IReadOnlyList<HunkLine> lines)
	{
		OldStart = oldStart;
		OldLength = oldLength;
		NewStart = newStart;
		NewLength = newLength;
		Lines = lines;
	}
}
