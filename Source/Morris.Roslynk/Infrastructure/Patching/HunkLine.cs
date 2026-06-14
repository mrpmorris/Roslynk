namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>One line of a hunk: its role and its text content with the leading marker stripped.</summary>
public sealed record HunkLine(HunkLineKind Kind, string Text);
