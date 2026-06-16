using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Writes the body shared by the write tools (rename, change_signature, remove_unused_usings, apply_code_*):
/// the blank separator then one solution-relative changed-file path per line, de-duplicated and sorted. The
/// caller writes the '#applied' and tool-specific headers first; this only renders the file list.
/// </summary>
public static class ChangedFilesOutline
{
	public static void Write(OutlineBuilder builder, IReadOnlyList<string> changedPaths, string? solutionDirectory)
	{
		builder.BeginBody();

		IEnumerable<string> relative = changedPaths
			.Select(path => SolutionRelativePath.Of(solutionDirectory, path) ?? path)
			.Distinct(StringComparer.Ordinal)
			.OrderBy(path => path, StringComparer.Ordinal);

		foreach (string path in relative)
			builder.Line(0, path);
	}
}
