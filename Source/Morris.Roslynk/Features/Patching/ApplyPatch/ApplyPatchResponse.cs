namespace Morris.Roslynk.Features.Patching.ApplyPatch;

/// <summary>A file the patch changed (or would change), with its resulting <c>documentVersion</c>.</summary>
public sealed record ApplyPatchChange(string Path, string Version);

/// <summary>
/// A target that moved on disk since the patch was based. Carries the current version and current text so
/// the model can rebase just this file in one step rather than re-reading the whole solution.
/// </summary>
public sealed record ApplyPatchStaleFile(string Path, string CurrentVersion, string CurrentText);

/// <summary>
/// The outcome of <c>apply_patch</c>. On <see cref="ApplyPatchOutcome.Applied"/> / <c>Preview</c>,
/// <c>ChangedFiles</c> lists the affected files with their new versions. On <c>Stale</c>,
/// <c>StaleFiles</c> carries the self-healing rebase data; on <c>NotSupported</c>, <c>RejectedFiles</c>
/// names the offending targets. A <c>Message</c> explains any non-applied outcome.
/// </summary>
public sealed record ApplyPatchResponse(
	ApplyPatchOutcome Outcome,
	IReadOnlyList<ApplyPatchChange> ChangedFiles,
	IReadOnlyList<ApplyPatchStaleFile> StaleFiles,
	IReadOnlyList<string> RejectedFiles,
	string? Message)
{
	public static ApplyPatchResponse NotSupported(IReadOnlyList<string> rejected) =>
		new(ApplyPatchOutcome.NotSupported, [], [], rejected,
			"apply_patch edits existing solution-compiled .cs files only; file creation/deletion and non-source targets are not supported.");

	public static ApplyPatchResponse Stale(IReadOnlyList<ApplyPatchStaleFile> stale) =>
		new(ApplyPatchOutcome.Stale, [], stale, [],
			"Some targets changed on disk since the patch was based; rebase against the returned current text and retry.");

	public static ApplyPatchResponse Failed(string message) =>
		new(ApplyPatchOutcome.PatchFailed, [], [], [], message);
}
