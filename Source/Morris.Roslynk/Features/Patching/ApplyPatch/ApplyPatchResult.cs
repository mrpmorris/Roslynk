using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Patching.ApplyPatch;

/// <summary>A file the patch changed (or would change), with its resulting <c>documentVersion</c>.</summary>
public sealed class ApplyPatchChange
{
	public string FilePath { get; }
	public string Version { get; }

	public ApplyPatchChange(string filePath, string version)
	{
		FilePath = filePath;
		Version = version;
	}
}

/// <summary>
/// A target that moved on disk since the patch was based. Carries the current version and current text so
/// the model can rebase just this file in one step rather than re-reading the whole solution.
/// </summary>
public sealed class ApplyPatchStaleFile
{
	public string Path { get; }
	public string CurrentVersion { get; }
	public string CurrentText { get; }

	public ApplyPatchStaleFile(string path, string currentVersion, string currentText)
	{
		Path = path;
		CurrentVersion = currentVersion;
		CurrentText = currentText;
	}
}

/// <summary>
/// The outcome of <c>apply_patch</c>. On success <see cref="Applied"/> is true — or false for a checkOnly
/// preview — and <see cref="ChangedFiles"/> lists the affected files with their new versions. Failures are
/// carried on <see cref="ResultBase.Error"/>: Stale (the self-healing rebase data also in
/// <see cref="StaleFiles"/>), NotSupported (offending targets in <see cref="RejectedFiles"/>), Conflict
/// when a hunk no longer matches, and Invalid when the patch cannot be parsed.
/// </summary>
public sealed class ApplyPatchResult : ResultBase
{
	public bool Applied { get; }
	public IReadOnlyList<ApplyPatchChange>? ChangedFiles { get; }
	public IReadOnlyList<ApplyPatchStaleFile>? StaleFiles { get; }
	public IReadOnlyList<string>? RejectedFiles { get; }

	public ApplyPatchResult(
		string solutionCurrentSnapshotId,
		SolutionStatus status,
		Error? error,
		bool applied,
		IReadOnlyList<ApplyPatchChange>? changedFiles,
		IReadOnlyList<ApplyPatchStaleFile>? staleFiles,
		IReadOnlyList<string>? rejectedFiles)
		: base(solutionCurrentSnapshotId, status, error)
	{
		Applied = applied;
		ChangedFiles = changedFiles;
		StaleFiles = staleFiles;
		RejectedFiles = rejectedFiles;
	}
}
