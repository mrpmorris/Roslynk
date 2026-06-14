namespace Morris.Roslynk.Features.Patching.ApplyPatch;

/// <summary>How an <c>apply_patch</c> call ended.</summary>
public enum ApplyPatchOutcome
{
	/// <summary>The patch was written to disk and the snapshot advanced.</summary>
	Applied,

	/// <summary>A <c>checkOnly</c> preview: the patch applies cleanly but nothing was written.</summary>
	Preview,

	/// <summary>One or more targets changed on disk since the patch was based; nothing was written.</summary>
	Stale,

	/// <summary>A target is not an editable solution-compiled <c>.cs</c> file; nothing was written.</summary>
	NotSupported,

	/// <summary>A hunk's context no longer matches the current file; nothing was written.</summary>
	PatchFailed,
}
