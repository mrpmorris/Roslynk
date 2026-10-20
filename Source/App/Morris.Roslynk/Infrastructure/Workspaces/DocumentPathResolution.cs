using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// The one classification of a caller-supplied document path every tool shares: <see cref="Resolved"/> when
/// the path names a loaded file, otherwise the <see cref="Error"/> the tool returns. Several loaded files
/// matching the same path is <c>Ambiguous</c> with the matching solution-relative paths as candidates, so a
/// caller can retry with one of them verbatim; a genuine miss stays <c>NotFound</c> with the tool's own
/// wording and <see cref="DocumentPathResolver.Candidates"/>.
/// </summary>
public sealed record DocumentPathResolution(ResolvedDocumentPath? Resolved, Error? Error)
{
	public bool IsResolved => Resolved is not null;

	/// <summary>
	/// Classifies <paramref name="path"/> against <paramref name="solution"/>. <paramref name="notFoundMessage"/>
	/// is the tool's existing wording for a genuine miss, so callers keep their established contract there.
	/// </summary>
	public static DocumentPathResolution Of(Solution solution, string? path, string notFoundMessage)
	{
		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(solution, path);
		if (resolved is not null)
			return new(resolved, null);

		IReadOnlyList<string> matches = DocumentPathResolver.Matches(solution, path);
		if (matches.Count > 1)
		{
			string requested = path?.Trim() ?? "";
			return new(
				null,
				Error.Ambiguous(
					$"'{requested}' matches {matches.Count} loaded files. Retry with one of the candidate paths below, exactly as written.",
					matches));
		}

		return new(null, Error.NotFound(notFoundMessage, DocumentPathResolver.Candidates(solution, path)));
	}
}
