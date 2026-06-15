using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Patching;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.Patching.ApplyPatch;

[McpServerToolType]
public sealed class ApplyPatchTool
{
	public const string ApplyPatchName = "apply_patch";

	private readonly InstanceRegistry InstanceRegistry;

	public ApplyPatchTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = ApplyPatchName,
		Title = "Apply a patch",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		"""
		Applies a git unified diff to solution-compiled .cs files, located by content (not line numbers) and
		written atomically. Prefer this over the host's raw file edit for .cs so the in-memory model stays in
		sync. Hunk headers may omit line numbers (a bare '@@'); a content-anchored hunk must match exactly one
		place, so include enough surrounding context that it is unambiguous. Edits existing files only — file
		creation/deletion and non-.cs targets are rejected. Pass baseVersions (the documentVersion each file
		was read at) to be told if a file moved since; pass checkOnly to preview the changed files without
		writing.
		""")]
	public async Task<ApplyPatchResult> ApplyPatch(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("A git unified diff (--- / +++ / @@ hunks) targeting one or more .cs files.")] string patch,
		[Description("Optional: the documentVersion each touched file was based on, to detect external edits.")] IReadOnlyList<FileVersion>? baseVersions = null,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		ApplyPatchResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, applied: false, changedFiles: null, staleFiles: null, rejectedFiles: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;

		IReadOnlyList<FilePatch> patches = UnifiedDiffParser.Parse(patch);
		if (patches.Count == 0)
			return Failure(Error.Invalid("No file sections were found in the patch."));

		FilePatch? hunkless = patches.FirstOrDefault(filePatch => filePatch.Hunks.Count == 0);
		if (hunkless is not null)
			return Failure(Error.Invalid($"The patch for '{hunkless.NewPath ?? hunkless.OldPath ?? "(unknown)"}' contains no hunks; nothing would change."));

		var targets = new List<PatchTarget>();
		var rejected = new List<string>();
		foreach (FilePatch filePatch in patches)
		{
			Document? document = filePatch.IsCreation || filePatch.IsDeletion
				? null
				: ResolveDocument(solution, filePatch.Path);

			if (document?.FilePath is null || !IsCSharp(document.FilePath))
				rejected.Add(filePatch.Path ?? "(unknown)");
			else
				targets.Add(new PatchTarget(filePatch, document.Id, document.FilePath));
		}

		if (rejected.Count > 0)
			return new ApplyPatchResult(
				model.SnapshotId,
				model.Status,
				Error.NotSupported(
					"apply_patch edits existing solution-compiled .cs files only; file creation/deletion and non-source targets are not supported."),
				applied: false,
				changedFiles: null,
				staleFiles: null,
				rejectedFiles: rejected);

		IReadOnlyDictionary<string, string> expectedVersions = BuildExpectedVersions(baseVersions);

		await instance.WriteLock.WaitAsync(cancellationToken);
		try
		{
			var stale = new List<ApplyPatchStaleFile>();
			var pending = new List<PendingPatch>();

			foreach (PatchTarget target in targets)
			{
				string diskText = await File.ReadAllTextAsync(target.FilePath, cancellationToken);
				string currentVersion = FileHash.Of(diskText);

				if (TryGetExpected(expectedVersions, target, out string? expected) && !string.Equals(expected, currentVersion, StringComparison.Ordinal))
				{
					stale.Add(new ApplyPatchStaleFile(target.FilePath, currentVersion, diskText));
					continue;
				}

				PatchApplyResult result = PatchApplier.Apply(diskText, target.FilePatch);
				if (!result.Success)
					return Failure(Error.Conflict($"{target.FilePath}: {result.FailureReason}"));

				pending.Add(new PendingPatch(target.DocumentId, target.FilePath, result.NewText!));
			}

			if (stale.Count > 0)
				return new ApplyPatchResult(
					model.SnapshotId,
					model.Status,
					Error.Stale(
						"Some targets changed on disk since the patch was based; rebase against the returned current text and retry.",
						stale.Select(file => file.Path).ToArray()),
					applied: false,
					changedFiles: null,
					staleFiles: stale,
					rejectedFiles: null);

			IReadOnlyList<ApplyPatchChange> changes = pending
				.Select(item => new ApplyPatchChange(item.FilePath, FileHash.Of(item.NewText)))
				.ToArray();

			if (checkOnly)
				return new ApplyPatchResult(model.SnapshotId, model.Status, error: null, applied: false, changedFiles: changes, staleFiles: null, rejectedFiles: null);

			await AtomicFileWriter.WriteAllAsync(
				pending.Select(item => new PendingWrite(item.FilePath, item.NewText)).ToArray(),
				cancellationToken);

			Solution updated = instance.CurrentSolution;
			foreach (PendingPatch item in pending)
			{
				if (updated.GetDocument(item.DocumentId) is not null)
					updated = updated.WithDocumentText(item.DocumentId, SourceText.From(item.NewText));
			}

			instance.AdvanceTo(updated);
			return new ApplyPatchResult(instance.CurrentModel.SnapshotId, instance.CurrentModel.Status, error: null, applied: true, changedFiles: changes, staleFiles: null, rejectedFiles: null);
		}
		finally
		{
			instance.WriteLock.Release();
		}
	}

	private static IReadOnlyDictionary<string, string> BuildExpectedVersions(IReadOnlyList<FileVersion>? baseVersions)
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (baseVersions is null)
			return map;

		foreach (FileVersion version in baseVersions)
			map[NormalizeSeparators(version.Path)] = version.Version;

		return map;
	}

	private static bool TryGetExpected(IReadOnlyDictionary<string, string> expected, PatchTarget target, out string? version)
	{
		foreach (string key in CandidateKeys(target))
		{
			if (expected.TryGetValue(key, out version))
				return true;
		}

		version = null;
		return false;
	}

	private static IEnumerable<string> CandidateKeys(PatchTarget target)
	{
		yield return NormalizeSeparators(target.FilePath);
		if (target.FilePatch.NewPath is not null)
			yield return NormalizeSeparators(target.FilePatch.NewPath);
		if (target.FilePatch.OldPath is not null)
			yield return NormalizeSeparators(target.FilePatch.OldPath);
		yield return NormalizeSeparators(System.IO.Path.GetFileName(target.FilePath));
	}

	private static Document? ResolveDocument(Solution solution, string? patchPath)
	{
		if (string.IsNullOrWhiteSpace(patchPath))
			return null;

		string normalized = NormalizeSeparators(patchPath);

		if (System.IO.Path.IsPathRooted(normalized))
		{
			Document? rooted = DocumentAt(solution, System.IO.Path.GetFullPath(normalized));
			if (rooted is not null)
				return rooted;
		}

		string? solutionDir = solution.FilePath is null ? null : System.IO.Path.GetDirectoryName(solution.FilePath);
		if (solutionDir is not null)
		{
			Document? relative = DocumentAt(solution, System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, normalized)));
			if (relative is not null)
				return relative;
		}

		Document? suffixMatch = null;
		int matches = 0;
		foreach (Document document in solution.Projects.SelectMany(project => project.Documents))
		{
			if (document.FilePath is not null && PathEndsWith(document.FilePath, normalized))
			{
				suffixMatch = document;
				matches++;
			}
		}

		return matches == 1 ? suffixMatch : null;
	}

	private static Document? DocumentAt(Solution solution, string fullPath)
	{
		foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(fullPath))
		{
			Document? document = solution.GetDocument(id);
			if (document is not null)
				return document;
		}

		return null;
	}

	private static bool PathEndsWith(string fullPath, string relative)
	{
		string normalizedFull = NormalizeSeparators(fullPath);
		string suffix = System.IO.Path.DirectorySeparatorChar + relative;
		return normalizedFull.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalizedFull, relative, StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeSeparators(string path) =>
		path.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);

	private static bool IsCSharp(string path) =>
		string.Equals(System.IO.Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

	private readonly struct PatchTarget
	{
		public FilePatch FilePatch { get; }
		public DocumentId DocumentId { get; }
		public string FilePath { get; }

		public PatchTarget(FilePatch filePatch, DocumentId documentId, string filePath)
		{
			FilePatch = filePatch;
			DocumentId = documentId;
			FilePath = filePath;
		}
	}

	private readonly struct PendingPatch
	{
		public DocumentId DocumentId { get; }
		public string FilePath { get; }
		public string NewText { get; }

		public PendingPatch(DocumentId documentId, string filePath, string newText)
		{
			DocumentId = documentId;
			FilePath = filePath;
			NewText = newText;
		}
	}
}
