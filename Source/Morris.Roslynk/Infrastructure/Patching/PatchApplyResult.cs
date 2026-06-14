namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// The outcome of applying a <see cref="FilePatch"/> to a file's text: the new text on success, or a
/// reason on failure (typically a hunk whose context no longer matches the current content).
/// </summary>
public sealed record PatchApplyResult(bool Success, string? NewText, string? FailureReason)
{
	public static PatchApplyResult Ok(string newText) => new(Success: true, newText, FailureReason: null);

	public static PatchApplyResult Fail(string reason) => new(Success: false, NewText: null, reason);
}
