using System.Text.RegularExpressions;

namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// Parses a git-style unified diff into <see cref="FilePatch"/>es. It is lenient about the surrounding
/// noise (<c>diff --git</c>, <c>index</c>, mode lines) and reads each hunk body by the line counts in its
/// <c>@@</c> header, so trailing prose after a patch does not bleed into a hunk. A bare <c>@@</c> header
/// with no <c>-l,s +l,s</c> ranges is also accepted (this tool locates hunks by content): the body is then
/// read up to the next structural delimiter and the lengths are derived from it.
/// </summary>
public static partial class UnifiedDiffParser
{
	public static IReadOnlyList<FilePatch> Parse(string patchText)
	{
		if (patchText is null)
			throw new ArgumentNullException(nameof(patchText));

		string[] lines = patchText.Replace("\r\n", "\n").Split('\n');
		var files = new List<FilePatch>();

		string? oldPath = null;
		string? newPath = null;
		bool isCreation = false;
		bool isDeletion = false;
		List<Hunk>? hunks = null;

		void FlushFile()
		{
			if (hunks is not null)
				files.Add(new FilePatch(oldPath, newPath, isCreation, isDeletion, hunks));
		}

		int i = 0;
		while (i < lines.Length)
		{
			string line = lines[i];

			if (line.StartsWith("--- ", StringComparison.Ordinal))
			{
				FlushFile();
				(oldPath, bool oldDevNull) = ParsePath(line[4..]);
				newPath = null;
				isCreation = oldDevNull;
				isDeletion = false;
				hunks = new List<Hunk>();

				if (i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
				{
					(newPath, bool newDevNull) = ParsePath(lines[i + 1][4..]);
					isDeletion = newDevNull;
					i++;
				}

				i++;
				continue;
			}

			if (line.StartsWith("@@", StringComparison.Ordinal) && hunks is not null)
			{
				Match header = HunkHeaderRegex().Match(line);

				// A bare "@@" (no line numbers) is accepted as a content-anchored hunk: read its body up to
				// the next delimiter and derive the lengths from it. A ranged header is parsed as before.
				int oldStart = header.Success ? int.Parse(header.Groups[1].Value) : 1;
				int newStart = header.Success ? int.Parse(header.Groups[3].Value) : 1;
				int? oldLength = header.Success ? (header.Groups[2].Success ? int.Parse(header.Groups[2].Value) : 1) : null;
				int? newLength = header.Success ? (header.Groups[4].Success ? int.Parse(header.Groups[4].Value) : 1) : null;

				(IReadOnlyList<HunkLine> body, int consumed) = ReadHunkBody(lines, i + 1, oldLength, newLength);
				if (body.Count > 0)
				{
					int resolvedOld = oldLength ?? body.Count(hunkLine => hunkLine.Kind != HunkLineKind.Added);
					int resolvedNew = newLength ?? body.Count(hunkLine => hunkLine.Kind != HunkLineKind.Removed);
					hunks.Add(new Hunk(oldStart, resolvedOld, newStart, resolvedNew, header.Success, body));
				}

				i += 1 + consumed;
				continue;
			}

			i++;
		}

		FlushFile();
		return files;
	}

	private static (IReadOnlyList<HunkLine> Lines, int Consumed) ReadHunkBody(string[] lines, int start, int? oldLength, int? newLength)
	{
		var body = new List<HunkLine>();
		int oldSeen = 0;
		int newSeen = 0;
		int i = start;

		while (i < lines.Length)
		{
			// When the header declared line counts, stop once both are satisfied.
			if (oldLength is int ol && newLength is int nl && oldSeen >= ol && newSeen >= nl)
				break;

			string line = lines[i];

			// A blank line in the file is encoded as " " (a lone space), so a truly-empty line is not a
			// hunk body line — it is the artifact of the patch's final newline, ending the hunk.
			if (line.Length == 0)
				break;

			// With no declared counts the body runs to the next structural delimiter, so a following file
			// or hunk header is not mistaken for a removed/added body line.
			if ((oldLength is null || newLength is null) && IsStructuralDelimiter(lines, i))
				break;

			char marker = line[0];
			string text = line[1..];

			switch (marker)
			{
				case ' ':
					body.Add(new HunkLine(HunkLineKind.Context, text));
					oldSeen++;
					newSeen++;
					break;
				case '+':
					body.Add(new HunkLine(HunkLineKind.Added, text));
					newSeen++;
					break;
				case '-':
					body.Add(new HunkLine(HunkLineKind.Removed, text));
					oldSeen++;
					break;
				case '\\':
					// "\ No newline at end of file" — trailing-newline state is preserved from the target
					// file itself, so this marker is informational only.
					break;
				default:
					return (body, i - start);
			}

			i++;
		}

		return (body, i - start);
	}

	private static bool IsStructuralDelimiter(string[] lines, int i)
	{
		string line = lines[i];
		return line.StartsWith("@@", StringComparison.Ordinal)
			|| line.StartsWith("diff --git ", StringComparison.Ordinal)
			|| (line.StartsWith("--- ", StringComparison.Ordinal)
				&& i + 1 < lines.Length
				&& lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal));
	}

	private static (string? Path, bool DevNull) ParsePath(string raw)
	{
		int tab = raw.IndexOf('\t');
		if (tab >= 0)
			raw = raw[..tab];
		raw = raw.Trim();

		if (raw == "/dev/null")
			return (null, true);

		if (raw.StartsWith("a/", StringComparison.Ordinal) || raw.StartsWith("b/", StringComparison.Ordinal))
			raw = raw[2..];

		return (raw, false);
	}

	[GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
	private static partial Regex HunkHeaderRegex();
}
