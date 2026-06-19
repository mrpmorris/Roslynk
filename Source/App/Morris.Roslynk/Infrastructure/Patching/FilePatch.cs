namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// All hunks targeting one file, with the paths from the <c>---</c> / <c>+++</c> headers (the <c>a/</c>
/// and <c>b/</c> prefixes stripped). A <c>/dev/null</c> on either side marks a file creation or deletion,
/// which the apply tool rejects in v1 (it edits existing solution-compiled <c>.cs</c> documents only).
/// </summary>
public sealed class FilePatch
{
	public string? OldPath { get; }
	public string? NewPath { get; }
	public bool IsCreation { get; }
	public bool IsDeletion { get; }
	public IReadOnlyList<Hunk> Hunks { get; }

	/// <summary>The path the patch targets: the new path for an edit, the old path for a deletion.</summary>
	public string? Path => IsDeletion ? OldPath : NewPath;

	public FilePatch(string? oldPath, string? newPath, bool isCreation, bool isDeletion, IReadOnlyList<Hunk> hunks)
	{
		OldPath = oldPath;
		NewPath = newPath;
		IsCreation = isCreation;
		IsDeletion = isDeletion;
		Hunks = hunks;
	}
}
