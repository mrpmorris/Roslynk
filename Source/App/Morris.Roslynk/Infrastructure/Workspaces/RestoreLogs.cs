using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// NuGet restore records its log entries — with their code and level — in each project's
/// <c>project.assets.json</c>. <see cref="MSBuildWorkspace"/> reports the same text as a
/// <see cref="WorkspaceDiagnostic"/> of <see cref="WorkspaceDiagnosticKind.Failure"/>, so the assets file
/// is the only place that can say a message is a warning (NU1902/NU1903/NU1510 …) and not a load failure.
/// </summary>
internal static class RestoreLogs
{
	internal const string AssetsFileName = "project.assets.json";

	/// <summary>What <see cref="MSBuildWorkspace"/> appends to the project file path in a failure's message.</summary>
	internal const string FailureMessageMarker = "with message: ";

	/// <summary>How deep the fallback scan of the solution directory descends.</summary>
	internal const int ScanMaxDepth = 8;

	/// <summary>How many files the fallback scan inspects before it gives up, so a large tree cannot stall a load.</summary>
	internal const int ScanMaxFiles = 500;

	/// <summary>
	/// Reads each loaded project's <c>obj/project.assets.json</c> and collects its Warning-level restore log
	/// entries, keyed by their message, with the first code seen for a message winning. When no project has
	/// that default intermediate path (an artifacts or custom intermediate layout), one bounded scan of
	/// <paramref name="solutionDirectory"/> looks for the files instead. A missing, unreadable or malformed
	/// file is skipped, so nothing here can fail a load.
	/// </summary>
	internal static IReadOnlyDictionary<string, string> ReadWarnings(IEnumerable<string?> projectFilePaths, string? solutionDirectory)
	{
		var warnings = new Dictionary<string, string>(StringComparer.Ordinal);
		var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bool foundDefaultPath = false;

		foreach (string? projectFilePath in projectFilePaths)
		{
			string? assetsPath = DefaultAssetsPath(projectFilePath);
			if (assetsPath is null || !File.Exists(assetsPath))
				continue;

			foundDefaultPath = true;
			if (read.Add(assetsPath))
				AddWarnings(assetsPath, warnings);
		}

		if (!foundDefaultPath && !string.IsNullOrEmpty(solutionDirectory))
		{
			foreach (string assetsPath in AssetsFilesUnder(solutionDirectory))
			{
				if (read.Add(assetsPath))
					AddWarnings(assetsPath, warnings);
			}
		}

		return warnings;
	}

	/// <summary>
	/// The text of a workspace failure message as a warning, <c>&lt;code&gt;: &lt;message&gt;</c>, when what
	/// follows <see cref="FailureMessageMarker"/> is exactly a Warning-level restore message; null when the
	/// failure is not one, so it stays a load diagnostic.
	/// </summary>
	internal static string? Classify(string workspaceMessage, IReadOnlyDictionary<string, string> warnings)
	{
		int marker = workspaceMessage.IndexOf(FailureMessageMarker, StringComparison.Ordinal);
		if (marker < 0)
			return null;

		string text = workspaceMessage[(marker + FailureMessageMarker.Length)..];
		return warnings.TryGetValue(text, out string? code) ? $"{code}: {text}" : null;
	}

	/// <summary>
	/// Splits the load's workspace failures into the restore warnings among them and the messages that
	/// remain load diagnostics, both in the order they were reported. A warning several projects report
	/// with the same text is listed once: the text names no project, so a repeat carries nothing.
	/// </summary>
	internal static (IReadOnlyList<string> Warnings, IReadOnlyList<string> Failures) Split(
		IEnumerable<WorkspaceDiagnostic> failures,
		IReadOnlyDictionary<string, string> warnings)
	{
		var matched = new List<string>();
		var remaining = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (WorkspaceDiagnostic failure in failures)
		{
			string? warning = Classify(failure.Message, warnings);
			if (warning is null)
				remaining.Add(failure.Message);
			else if (seen.Add(warning))
				matched.Add(warning);
		}

		return (matched, remaining);
	}

	/// <summary>The assets file of a project's default intermediate folder, <c>obj</c>, or null when the project has no path.</summary>
	private static string? DefaultAssetsPath(string? projectFilePath)
	{
		if (string.IsNullOrEmpty(projectFilePath))
			return null;

		string? directory = Path.GetDirectoryName(projectFilePath);
		return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "obj", AssetsFileName);
	}

	private static void AddWarnings(string assetsPath, Dictionary<string, string> warnings)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(File.ReadAllText(assetsPath));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
		{
			return;
		}

		using (document)
		{
			if (!document.RootElement.TryGetProperty("logs", out JsonElement logs) || logs.ValueKind != JsonValueKind.Array)
				return;

			foreach (JsonElement entry in logs.EnumerateArray())
			{
				if (entry.ValueKind != JsonValueKind.Object
					|| !entry.TryGetProperty("level", out JsonElement level)
					|| level.ValueKind != JsonValueKind.String
					|| !string.Equals(level.GetString(), "Warning", StringComparison.OrdinalIgnoreCase))
					continue;

				if (!entry.TryGetProperty("message", out JsonElement message)
					|| message.ValueKind != JsonValueKind.String
					|| !entry.TryGetProperty("code", out JsonElement code)
					|| code.ValueKind != JsonValueKind.String)
					continue;

				string? text = message.GetString();
				string? codeText = code.GetString();
				if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(codeText))
					continue;

				warnings.TryAdd(text, codeText);
			}
		}
	}

	/// <summary>
	/// The assets files under <paramref name="directory"/>, skipping build output and tooling folders and
	/// stopping after <see cref="ScanMaxFiles"/> inspected files. Directory enumeration failures skip the
	/// folder rather than failing the load.
	/// </summary>
	private static IEnumerable<string> AssetsFilesUnder(string directory)
	{
		var pending = new Stack<(string Directory, int Depth)>();
		pending.Push((directory, 0));
		int inspected = 0;

		while (pending.Count > 0)
		{
			(string current, int depth) = pending.Pop();

			string[] files;
			try
			{
				files = Directory.GetFiles(current);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (string file in files)
			{
				if (++inspected > ScanMaxFiles)
					yield break;

				if (string.Equals(Path.GetFileName(file), AssetsFileName, StringComparison.OrdinalIgnoreCase))
					yield return file;
			}

			if (depth >= ScanMaxDepth)
				continue;

			string[] children;
			try
			{
				children = Directory.GetDirectories(current);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (string child in children)
			{
				if (IsSkippedFolder(Path.GetFileName(child)))
					continue;

				pending.Push((child, depth + 1));
			}
		}
	}

	private static bool IsSkippedFolder(string name) =>
		name.Equals("bin", StringComparison.OrdinalIgnoreCase)
		|| name.Equals(".git", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);
}
