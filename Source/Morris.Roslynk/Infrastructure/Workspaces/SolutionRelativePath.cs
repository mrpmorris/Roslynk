using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Expresses an absolute source path relative to the solution's directory, so results carry short,
/// portable paths instead of machine-specific absolute ones. A file outside the solution folder (a linked
/// document) walks out with '..'. Emitted paths always use '/' as the separator regardless of OS; a
/// null/empty path is returned as-is. <see cref="ToAbsolute"/> is the inverse for caller-supplied paths.
/// </summary>
public static class SolutionRelativePath
{
	public static string? DirectoryOf(Solution solution) =>
		solution.FilePath is null ? null : Path.GetDirectoryName(solution.FilePath);

	public static string? Of(string? solutionDirectory, string? absolutePath)
	{
		if (string.IsNullOrEmpty(absolutePath))
			return absolutePath;

		string relative = string.IsNullOrEmpty(solutionDirectory)
			? absolutePath
			: Path.GetRelativePath(solutionDirectory, absolutePath);

		return relative.Replace('\\', '/');
	}

	/// <summary>
	/// The inverse of <see cref="Of"/>: resolves a caller-supplied path to an absolute one. A rooted path
	/// is returned as-is; a relative path is resolved against the solution directory (so the relative paths
	/// this type emits round-trip back in), falling back to the current directory when none is known.
	/// </summary>
	public static string ToAbsolute(string? solutionDirectory, string path)
	{
		string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
		if (Path.IsPathRooted(normalized) || string.IsNullOrEmpty(solutionDirectory))
			return Path.GetFullPath(normalized);

		return Path.GetFullPath(Path.Combine(solutionDirectory, normalized));
	}
}
