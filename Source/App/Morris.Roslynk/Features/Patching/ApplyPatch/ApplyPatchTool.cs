using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Patching;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
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

	[Description("""
		Applies content-anchored git unified diffs to existing text files. Targets: solution-compiled
		.cs/.razor/.cshtml (model synchronized), plus other text files under the solution folder
		(e.g. .csproj/.json/.md). Creation/deletion, binary files and paths outside that folder:
		NotSupported; missing files: NotFound.

		Header-only result: applied=<Y|N>. Hunk headers may omit line numbers (bare '@@'); each hunk
		must match exactly once, so provide unique context. Unique-suffix paths also resolve, adding
		resolvedPath=<solution-relative path>.
		Ambiguous paths return candidate=<path>; rejections list rejected=<path>.
		Unmatched/ambiguous hunks: Conflict with file/reason; no file sections: Invalid.

		baseVersions pairs patch paths with documentVersion: SHA-256 hex of loaded text encoded as
		UTF-8 without BOM. No read tool reports this hash; supply only if you can compute it exactly.
		Mismatches return Stale with stale=<path>.
		""")]
	[McpServerTool(
		Name = ApplyPatchName,
		Title = "Apply a patch",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> ApplyPatch(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Git unified diff (---/+++/@@).")] string patch,
		[Description("Patch paths and expected documentVersion hashes.")] IReadOnlyList<FileVersion>? baseVersions = null,
		[Description(ParameterDescriptions.CheckOnly)] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		Solution solution = model.Solution;

		IReadOnlyList<FilePatch> patches = UnifiedDiffParser.Parse(patch);
		if (patches.Count == 0)
			return Failure(Error.Invalid("No file sections were found in the patch."));

		FilePatch? hunkless = patches.FirstOrDefault(filePatch => filePatch.Hunks.Count == 0);
		if (hunkless is not null)
			return Failure(Error.Invalid($"The patch for '{hunkless.NewPath ?? hunkless.OldPath ?? "(unknown)"}' contains no hunks; nothing would change."));

		var targets = new List<PatchTarget>();
		var rejected = new List<Rejection>();
		foreach (FilePatch filePatch in patches)
		{
			if (filePatch.IsCreation || filePatch.IsDeletion)
			{
				rejected.Add(new Rejection(filePatch.Path ?? "(unknown)", RejectionKind.NotSupported, []));
				continue;
			}

			TargetResolution resolution = ResolveTarget(solution, filePatch.Path);
			if (resolution.FilePath is null)
				rejected.Add(resolution.Rejection ?? new Rejection(filePatch.Path ?? "(unknown)", RejectionKind.NotSupported, []));
			else
				targets.Add(new PatchTarget(filePatch, resolution.FilePath, resolution.ResolvedPath));
		}

		if (rejected.Count > 0)
		{
			return FormatRejections(rejected, solution, model.Status);
		}

		IReadOnlyDictionary<string, string> expectedVersions = BuildExpectedVersions(baseVersions);

		const string staleMessage = "Some targets changed on disk since the patch was based; re-read the file and retry.";

		string Applied(bool applied)
		{
			var builder = new OutlineBuilder();
			builder.Header("applied", applied);
			foreach (PatchTarget target in targets)
			{
				if (target.ResolvedPath is string resolved)
					builder.Header("resolvedPath", resolved);
			}
			builder.Status(instance.CurrentModel.Status);
			return builder.ToString();
		}

		if (checkOnly)
		{
			PatchComputation preview = Compute(targets, expectedVersions);
			if (preview.Conflict is not null)
				return Failure(Error.Conflict(preview.Conflict));
			if (preview.Stale.Count > 0)
				return Failure(Error.Stale(staleMessage, preview.Stale));

			return Applied(false);
		}

		try
		{
			await instance.EnqueueWriteAsync(async (current, token) =>
			{
				PatchComputation computation = Compute(targets, expectedVersions);
				if (computation.Conflict is not null)
					throw new PatchConflictException(computation.Conflict);
				if (computation.Stale.Count > 0)
					throw new PatchStaleException(computation.Stale);

				await AtomicFileWriter.WriteAllAsync(
					computation.Pending.Select(item => new PendingWrite(item.FilePath, item.NewText, item.Encoding)).ToArray(),
					token);

				Solution updated = current;
				foreach (PendingPatch item in computation.Pending)
				{
					// A compiled .cs document or a .razor/.cshtml additional document is folded into the
					// snapshot by path; a plain text file is not in the model and is written to disk only.
					foreach (DocumentId id in updated.GetDocumentIdsWithFilePath(item.FilePath))
					{
						if (updated.GetDocument(id) is not null)
							updated = updated.WithDocumentText(id, SourceText.From(item.NewText));
						else if (updated.GetAdditionalDocument(id) is not null)
							updated = updated.WithAdditionalDocumentText(id, SourceText.From(item.NewText));
					}
				}

				return new WriteResult(updated, computation.Pending.Select(item => item.FilePath).ToArray());
			}, cancellationToken);

			return Applied(true);
		}
		catch (PatchStaleException stale)
		{
			return Failure(Error.Stale(staleMessage, stale.Paths));
		}
		catch (PatchConflictException conflict)
		{
			return Failure(Error.Conflict(conflict.Message));
		}
	}

	private static PatchComputation Compute(IReadOnlyList<PatchTarget> targets, IReadOnlyDictionary<string, string> expectedVersions)
	{
		var stale = new List<string>();
		var pending = new List<PendingPatch>();

		foreach (PatchTarget target in targets)
		{
			(string diskText, Encoding encoding) = ReadTextPreservingEncoding(target.FilePath);
			string currentVersion = FileHash.Of(diskText);

			if (TryGetExpected(expectedVersions, target, out string? expected) && !string.Equals(expected, currentVersion, StringComparison.Ordinal))
			{
				stale.Add(target.FilePath);
				continue;
			}

			PatchApplyResult result = PatchApplier.Apply(diskText, target.FilePatch);
			if (!result.Success)
				return PatchComputation.FromConflict($"{target.FilePath}: {result.FailureReason}");

			pending.Add(new PendingPatch(target.FilePath, result.NewText!, encoding));
		}

		return stale.Count > 0 ? PatchComputation.FromStale(stale) : PatchComputation.FromPending(pending);
	}

	/// <summary>
	/// One error for all rejected files. When they failed for different reasons the code is the most
	/// actionable one (NotFound, then Ambiguous, then NotSupported) and the message explains every reason.
	/// </summary>
	private static string FormatRejections(IReadOnlyList<Rejection> rejected, Solution solution, SolutionStatus status)
	{
		string solutionDirectory = SolutionRelativePath.DirectoryOf(solution) ?? "(unknown)";
		RejectionKind[] kinds = [.. rejected.Select(item => item.Kind).Distinct().Order()];

		string Message(RejectionKind kind) => kind switch
		{
			RejectionKind.NotFound => $"The file does not exist. Patch paths are relative to the solution folder '{solutionDirectory}'.",
			RejectionKind.Ambiguous => $"The path matches several files; use a longer path relative to the solution folder '{solutionDirectory}'.",
			_ => "apply_patch edits existing text files inside the solution folder only; file creation/deletion, binary files, and paths outside the solution are not supported."
		};

		ErrorCode code = kinds[0] switch
		{
			RejectionKind.NotFound => ErrorCode.NotFound,
			RejectionKind.Ambiguous => ErrorCode.Ambiguous,
			_ => ErrorCode.NotSupported
		};

		var builder = new OutlineBuilder();
		builder.Header("error", code.ToString());
		builder.Header("errorMessage", string.Join(" ", kinds.Select(Message)));
		foreach (Rejection item in rejected)
			builder.Header("rejected", item.Path);
		foreach (string candidate in rejected.SelectMany(item => item.Candidates).Distinct())
			builder.Header("candidate", candidate);
		builder.Status(status);
		return builder.ToString();
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

	/// <summary>
	/// Resolves a patch header path to an existing writable text file: a regular document or additional
	/// document (.razor/.cshtml) in the loaded solution first, then any text file under the solution folder.
	/// </summary>
	private static TargetResolution ResolveTarget(Solution solution, string? patchPath)
	{
		if (string.IsNullOrWhiteSpace(patchPath))
			return TargetResolution.Rejected("(unknown)", RejectionKind.NotSupported);

		ResolvedDocumentPath? modelPath = DocumentPathResolver.Resolve(solution, patchPath);
		if (modelPath is not null)
			return TargetResolution.Resolved(modelPath.AbsolutePath, modelPath.ResolvedBySuffix ? modelPath.RelativePath : null);

		string? solutionDir = SolutionRelativePath.DirectoryOf(solution);
		if (solutionDir is null)
			return TargetResolution.Rejected(patchPath, RejectionKind.NotSupported);

		string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, NormalizeSeparators(patchPath)));
		if (!IsUnder(fullPath, solutionDir))
			return TargetResolution.Rejected(patchPath, RejectionKind.NotSupported);
		if (!File.Exists(fullPath))
		{
			IReadOnlyList<string> ambiguous = DocumentPathResolver.Matches(solution, patchPath);
			return ambiguous.Count > 0
				? TargetResolution.Rejected(patchPath, RejectionKind.Ambiguous, ambiguous)
				: TargetResolution.Rejected(patchPath, RejectionKind.NotFound);
		}
		if (IsBuildOutput(fullPath))
			return TargetResolution.Rejected(patchPath, RejectionKind.NotSupported);

		try
		{
			if (ReadTextPreservingEncoding(fullPath).Text.Contains('\0'))
				return TargetResolution.Rejected(patchPath, RejectionKind.NotSupported); // A NUL byte marks binary content.
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or DecoderFallbackException
			or ArgumentException)
		{
			// Unreadable or not decodable as text; not a patchable file.
			return TargetResolution.Rejected(patchPath, RejectionKind.NotSupported);
		}

		return TargetResolution.Resolved(fullPath, null);
	}

	private static string NormalizeSeparators(string path) =>
		path.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);

	private static bool IsUnder(string child, string ancestor) =>
		string.Equals(child, ancestor, StringComparison.OrdinalIgnoreCase)
		|| child.StartsWith(ancestor + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private static bool IsBuildOutput(string path) =>
		path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
			.Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Reads a file as text, returning the encoding to write it back with: a UTF-8 / UTF-16 BOM is detected
	/// and preserved, and a BOM-less file is decoded as strict UTF-8 (invalid bytes throw, so a binary file
	/// is never silently corrupted).
	/// </summary>
	private static (string Text, Encoding Encoding) ReadTextPreservingEncoding(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		Encoding encoding = FileEncoding.Detect(bytes);
		int preambleLength = encoding.Preamble.Length;

		string text = encoding is UnicodeEncoding
			? DecodeUtf16(bytes, encoding)
			: StrictUtf8.GetString(bytes, preambleLength, bytes.Length - preambleLength);
		return (text, encoding);
	}

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private static string DecodeUtf16(byte[] bytes, Encoding utf16)
	{
		int preambleLength = utf16.Preamble.Length;
		// UTF-16 encodes two bytes per code unit; a trailing lone byte is corrupt, treat the file as not text.
		if ((bytes.Length - preambleLength) % 2 != 0)
			throw new DecoderFallbackException("A UTF-16 file has an odd number of bytes after its BOM.");

		return utf16.GetString(bytes, preambleLength, bytes.Length - preambleLength);
	}

	/// <summary>Why a patch path was rejected, ordered by how actionable the error code is.</summary>
	private enum RejectionKind
	{
		NotFound,
		Ambiguous,
		NotSupported
	}

	private sealed record Rejection(string Path, RejectionKind Kind, IReadOnlyList<string> Candidates);

	private readonly struct TargetResolution
	{
		public string? FilePath { get; }
		public string? ResolvedPath { get; }
		public Rejection? Rejection { get; }

		private TargetResolution(string? filePath, string? resolvedPath, Rejection? rejection)
		{
			FilePath = filePath;
			ResolvedPath = resolvedPath;
			Rejection = rejection;
		}

		public static TargetResolution Resolved(string filePath, string? resolvedPath) => new(filePath, resolvedPath, null);

		public static TargetResolution Rejected(string path, RejectionKind kind, IReadOnlyList<string>? candidates = null) =>
			new(null, null, new Rejection(path, kind, candidates ?? []));
	}

	private readonly struct PatchTarget
	{
		public FilePatch FilePatch { get; }
		public string FilePath { get; }
		public string? ResolvedPath { get; }

		public PatchTarget(FilePatch filePatch, string filePath, string? resolvedPath)
		{
			FilePatch = filePatch;
			FilePath = filePath;
			ResolvedPath = resolvedPath;
		}
	}

	private readonly struct PendingPatch
	{
		public string FilePath { get; }
		public string NewText { get; }
		public Encoding? Encoding { get; }

		public PendingPatch(string filePath, string newText, Encoding? encoding)
		{
			FilePath = filePath;
			NewText = newText;
			Encoding = encoding;
		}
	}

	private sealed class PatchComputation
	{
		public IReadOnlyList<PendingPatch> Pending { get; }
		public IReadOnlyList<string> Stale { get; }
		public string? Conflict { get; }

		private PatchComputation(IReadOnlyList<PendingPatch> pending, IReadOnlyList<string> stale, string? conflict)
		{
			Pending = pending;
			Stale = stale;
			Conflict = conflict;
		}

		public static PatchComputation FromPending(IReadOnlyList<PendingPatch> pending) => new(pending, [], null);
		public static PatchComputation FromStale(IReadOnlyList<string> stale) => new([], stale, null);
		public static PatchComputation FromConflict(string message) => new([], [], message);
	}

	private sealed class PatchStaleException : Exception
	{
		public IReadOnlyList<string> Paths { get; }

		public PatchStaleException(IReadOnlyList<string> paths) => Paths = paths;
	}

	private sealed class PatchConflictException : Exception
	{
		public PatchConflictException(string message) : base(message)
		{
		}
	}
}