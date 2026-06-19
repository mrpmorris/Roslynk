namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// The outcome of applying a <see cref="FilePatch"/> to a file's text: the new text on success, or a
/// reason on failure (typically a hunk whose context no longer matches the current content).
/// </summary>
public sealed class PatchApplyResult
{
	public bool Success { get; }
	public string? NewText { get; }
	public string? FailureReason { get; }

	public static PatchApplyResult Ok(string newText) => new(success: true, newText, failureReason: null);

	public static PatchApplyResult Fail(string reason) => new(success: false, newText: null, reason);

	public PatchApplyResult(bool success, string? newText, string? failureReason)
	{
		Success = success;
		NewText = newText;
		FailureReason = failureReason;
	}
}
