namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>One line of a hunk: its role and its text content with the leading marker stripped.</summary>
public sealed class HunkLine
{
	public HunkLineKind Kind { get; }
	public string Text { get; }

	public HunkLine(HunkLineKind kind, string text)
	{
		Kind = kind;
		Text = text;
	}
}
