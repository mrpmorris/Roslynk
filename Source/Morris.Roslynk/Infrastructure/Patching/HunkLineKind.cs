namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>The role of a line inside a unified-diff hunk.</summary>
public enum HunkLineKind
{
	/// <summary>An unchanged line present on both sides (a leading space in the diff).</summary>
	Context,

	/// <summary>A line added by the patch (a leading <c>+</c>).</summary>
	Added,

	/// <summary>A line removed by the patch (a leading <c>-</c>).</summary>
	Removed,
}
