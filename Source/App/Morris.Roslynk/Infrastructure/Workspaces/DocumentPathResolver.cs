using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// A solution file named by a caller-supplied path. <see cref="RelativePath"/> is the canonical
/// solution-relative form every tool prints, and <see cref="ResolvedBySuffix"/> is true when the
/// caller's path was not an exact match and was resolved through the unique-suffix fallback.
/// </summary>
public sealed record ResolvedDocumentPath(string AbsolutePath, string RelativePath, bool ResolvedBySuffix);

/// <summary>
/// Maps a caller-supplied document path (absolute or solution-relative) to a file loaded in the
/// solution: a compiled document or an additional document (.razor/.cshtml). Exact matches win;
/// otherwise a path suffix that names exactly one file resolves, and failing that a bare file name
/// that names exactly one file. Several matches are never guessed - callers keep their NotFound
/// and offer <see cref="Candidates"/> instead.
/// </summary>
public static class DocumentPathResolver
{
	public static ResolvedDocumentPath? Resolve(Solution solution, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		string normalized = Normalize(path.Trim());
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);
		string full = SolutionRelativePath.ToAbsolute(solutionDirectory, normalized);

		if (IsLoaded(solution, full))
			return new ResolvedDocumentPath(full, SolutionRelativePath.Of(solutionDirectory, full)!, ResolvedBySuffix: false);

		string requested = SolutionRelativePath.Of(solutionDirectory, full)!;

		// A path suffix ('Pages/Invoices/ContractInvoices.razor.cs') is the strongest fallback; a bare
		// file name is only considered when no suffix matched at all.
		IReadOnlyList<string> matches = MatchesBySuffix(solution, solutionDirectory, requested);
		if (matches.Count == 0)
			matches = MatchesByName(solution, solutionDirectory, Path.GetFileName(requested));

		if (matches.Count != 1)
			return null;

		string relative = matches[0];
		return new ResolvedDocumentPath(
			SolutionRelativePath.ToAbsolute(solutionDirectory, relative),
			relative,
			ResolvedBySuffix: true);
	}

	/// <summary>
	/// Every solution file whose path suffix (or, when none do, whose file name) matches
	/// <paramref name="path"/>, as distinct solution-relative paths in ordinal order.
	/// </summary>
	public static IReadOnlyList<string> Matches(Solution solution, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return [];

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);
		string requested = SolutionRelativePath.Of(
			solutionDirectory,
			SolutionRelativePath.ToAbsolute(solutionDirectory, Normalize(path.Trim())))!;

		IReadOnlyList<string> matches = MatchesBySuffix(solution, solutionDirectory, requested);
		return matches.Count > 0
			? matches
			: MatchesByName(solution, solutionDirectory, Path.GetFileName(requested));
	}

	/// <summary>
	/// Paths to offer a caller whose path matched nothing: files whose relative path contains the
	/// requested one (or vice versa), or every loaded file when none are related, capped at 20.
	/// </summary>
	public static IReadOnlyList<string> Candidates(Solution solution, string? path)
	{
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);
		string requested = string.IsNullOrWhiteSpace(path)
			? ""
			: SolutionRelativePath.Of(
				solutionDirectory,
				SolutionRelativePath.ToAbsolute(solutionDirectory, Normalize(path.Trim())))!;

		SortedSet<string> all = new(StringComparer.Ordinal);
		SortedSet<string> related = new(StringComparer.Ordinal);
		foreach (string filePath in AllFilePaths(solution))
		{
			string relative = SolutionRelativePath.Of(solutionDirectory, filePath)!;
			all.Add(relative);
			if (relative.Contains(requested, StringComparison.OrdinalIgnoreCase)
				|| requested.Contains(relative, StringComparison.OrdinalIgnoreCase))
			{
				related.Add(relative);
			}
		}

		IEnumerable<string> result = related.Count > 0 ? related : all;
		return [.. result.Take(20)];
	}

	private static bool IsLoaded(Solution solution, string fullPath) =>
		solution.GetDocumentIdsWithFilePath(fullPath).Any(id =>
			solution.GetDocument(id) is not null || solution.GetAdditionalDocument(id) is not null);

	private static IEnumerable<string> AllFilePaths(Solution solution) =>
		solution.Projects
			.SelectMany(project => project.Documents.Select(document => document.FilePath)
				.Concat(project.AdditionalDocuments.Select(document => document.FilePath)))
			.OfType<string>()
			.Distinct(StringComparer.OrdinalIgnoreCase);

	private static IReadOnlyList<string> MatchesBySuffix(Solution solution, string? solutionDirectory, string requested)
	{
		string suffix = "/" + requested;
		return [.. AllFilePaths(solution)
			.Select(filePath => SolutionRelativePath.Of(solutionDirectory, filePath)!)
			.Where(relative => string.Equals(relative, requested, StringComparison.OrdinalIgnoreCase)
				|| relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.Ordinal)];
	}

	private static IReadOnlyList<string> MatchesByName(Solution solution, string? solutionDirectory, string fileName) =>
		string.IsNullOrEmpty(fileName)
			? []
			: [.. AllFilePaths(solution)
				.Where(filePath => string.Equals(Path.GetFileName(filePath), fileName, StringComparison.OrdinalIgnoreCase))
				.Select(filePath => SolutionRelativePath.Of(solutionDirectory, filePath)!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Order(StringComparer.Ordinal)];

	private static string Normalize(string path) =>
		path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
