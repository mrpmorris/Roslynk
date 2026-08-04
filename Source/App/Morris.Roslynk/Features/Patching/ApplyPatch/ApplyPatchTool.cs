using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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
		$"""
		Applies a git unified diff to text files, located by content (not line numbers) and written atomically.
		Targets: solution-compiled .cs files and .razor/.cshtml documents (kept in sync with the in-memory model),
		plus any other existing text file under the solution folder (e.g. .csproj, .json, .md), written to disk.
		Returns a header-only text result, not JSON: 'applied=<Y|N>' (and 'status' only when not Ready)
		on success (applied is N for a checkOnly preview). {OutlineDescriptions.Freshness} Prefer this over the host's raw file
		edit for .cs so the in-memory model stays in sync. Hunk headers may omit line numbers (a bare '@@'); a
		content-anchored hunk must match exactly one place, so include enough surrounding context that it is
		unambiguous. Edits existing files only; creation/deletion, binary files, and paths outside the solution
		folder are rejected as 'error=NotSupported' with 'rejected=<path>' lines. Pass baseVersions (the
		documentVersion each file was read at) to be told if a file moved since (returned as 'error=Stale' with
		'stale=<path>' lines); pass checkOnly to validate without writing.
		""")]
	public async Task<string> ApplyPatch(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("A git unified diff (--- / +++ / @@ hunks) targeting one or more text files.")] string patch,
		[Description("Optional: the documentVersion each touched file was based on, to detect external edits.")] IReadOnlyList<FileVersion>? baseVersions = null,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

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
			if (filePatch.IsCreation || filePatch.IsDeletion)
			{
				rejected.Add(filePatch.Path ?? "(unknown)");
				continue;
			}

			TargetResolution resolution = ResolveTarget(solution, filePatch.Path);
			if (resolution.FilePath is null)
				rejected.Add(resolution.RejectReason ?? filePatch.Path ?? "(unknown)");
			else
				targets.Add(new PatchTarget(filePatch, resolution.FilePath));
		}

		if (rejected.Count > 0)
		{
			var builder = new OutlineBuilder();
			builder.Header("error", ErrorCode.NotSupported.ToString());
			builder.Header("errorMessage", "apply_patch edits existing text files inside the solution folder only; file creation/deletion, binary files, and paths outside the solution are not supported.");
			foreach (string path in rejected)
				builder.Header("rejected", path);
			builder.Status(model.Status);
			return builder.ToString();
		}

		IReadOnlyDictionary<string, string> expectedVersions = BuildExpectedVersions(baseVersions);

		const string staleMessage = "Some targets changed on disk since the patch was based; re-read the file and retry.";

		string Applied(bool applied) =>
			new OutlineBuilder()
				.Header("applied", applied)
				.Status(instance.CurrentModel.Status)
				.ToString();

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
			return new TargetResolution(null, "(unknown)");

		string? modelPath = ResolveModelPath(solution, patchPath);
		if (modelPath is not null)
			return new TargetResolution(modelPath, null);

		string? solutionDir = solution.FilePath is null ? null : System.IO.Path.GetDirectoryName(solution.FilePath);
		if (solutionDir is null)
			return new TargetResolution(null, patchPath);

		string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, NormalizeSeparators(patchPath)));
		if (!IsUnder(fullPath, solutionDir))
			return new TargetResolution(null, patchPath);
		if (!File.Exists(fullPath))
			return new TargetResolution(null, patchPath);
		if (IsBuildOutput(fullPath))
			return new TargetResolution(null, patchPath);

		try
		{
			if (ReadTextPreservingEncoding(fullPath).Text.Contains('\0'))
				return new TargetResolution(null, patchPath); // A NUL byte marks binary content.
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or DecoderFallbackException
			or ArgumentException)
		{
			// Unreadable or not decodable as text; not a patchable file.
			return new TargetResolution(null, patchPath);
		}

		return new TargetResolution(fullPath, null);
	}

	private static string? ResolveModelPath(Solution solution, string patchPath)
	{
		string normalized = NormalizeSeparators(patchPath);

		if (System.IO.Path.IsPathRooted(normalized))
		{
			string? rooted = DocumentOrAdditionalAt(solution, System.IO.Path.GetFullPath(normalized));
			if (rooted is not null)
				return rooted;
		}

		string? solutionDir = solution.FilePath is null ? null : System.IO.Path.GetDirectoryName(solution.FilePath);
		if (solutionDir is not null)
		{
			string? relative = DocumentOrAdditionalAt(solution, System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, normalized)));
			if (relative is not null)
				return relative;
		}

		string? suffixMatch = null;
		int matches = 0;
		foreach (string filePath in AllDocumentPaths(solution))
		{
			if (PathEndsWith(filePath, normalized))
			{
				suffixMatch = filePath;
				matches++;
			}
		}

		return matches == 1 ? suffixMatch : null;
	}

	/// <summary>A regular or additional document at the given full path; the path itself on success.</summary>
	private static string? DocumentOrAdditionalAt(Solution solution, string fullPath)
	{
		foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(fullPath))
		{
			if (solution.GetDocument(id) is not null || solution.GetAdditionalDocument(id) is not null)
				return fullPath;
		}

		return null;
	}

	private static IEnumerable<string> AllDocumentPaths(Solution solution)
	{
		foreach (Project project in solution.Projects)
		{
			foreach (Document document in project.Documents)
			{
				if (document.FilePath is not null)
					yield return document.FilePath;
			}

			foreach (TextDocument document in project.AdditionalDocuments)
			{
				if (document.FilePath is not null)
					yield return document.FilePath;
			}
		}
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

		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			return (StrictUtf8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			return (DecodeUtf16(bytes, bigEndian: false), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			return (DecodeUtf16(bytes, bigEndian: true), new UnicodeEncoding(bigEndian: true, byteOrderMark: true));

		return (StrictUtf8.GetString(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private static string DecodeUtf16(byte[] bytes, bool bigEndian)
	{
		int preambleLength = 2;
		// UTF-16 encodes two bytes per code unit; a trailing lone byte is corrupt, treat the file as not text.
		if ((bytes.Length - preambleLength) % 2 != 0)
			throw new DecoderFallbackException("A UTF-16 file has an odd number of bytes after its BOM.");

		return new UnicodeEncoding(bigEndian, byteOrderMark: false).GetString(bytes, preambleLength, bytes.Length - preambleLength);
	}

	private readonly struct TargetResolution
	{
		public string? FilePath { get; }
		public string? RejectReason { get; }

		public TargetResolution(string? filePath, string? rejectReason)
		{
			FilePath = filePath;
			RejectReason = rejectReason;
		}
	}

	private readonly struct PatchTarget
	{
		public FilePatch FilePatch { get; }
		public string FilePath { get; }

		public PatchTarget(FilePatch filePatch, string filePath)
		{
			FilePatch = filePatch;
			FilePath = filePath;
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