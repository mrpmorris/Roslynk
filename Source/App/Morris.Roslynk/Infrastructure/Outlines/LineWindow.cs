namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Slices verbatim source text into line-bounded pages. Lines are '\n'-delimited with each '\r' kept
/// attached to its own line, so CRLF and LF sources both count one line per break and a page is a
/// byte-exact slice of the original text - a cut lands after a whole line's terminator, never inside one.
/// </summary>
/// <remarks>
/// The window never normalizes: <c>string.Split</c> plus a re-join would strip or rewrite the '\r' of CRLF
/// endings, so offsets are walked with <c>IndexOf('\n')</c> and the page is one substring.
/// </remarks>
internal static class LineWindow
{
	/// <summary>
	/// The number of lines in <paramref name="text"/>: one per '\n', plus a final line when the text does
	/// not end with one. Empty text has no lines.
	/// </summary>
	public static int CountLines(string text)
	{
		int lines = 0;
		int at = 0;
		while ((at = text.IndexOf('\n', at)) >= 0)
		{
			lines++;
			at++;
		}

		return text.Length > 0 && text[^1] != '\n' ? lines + 1 : lines;
	}

	/// <summary>
	/// Slices <paramref name="text"/> into the page that starts at 1-based <paramref name="startLine"/>,
	/// taking at most <paramref name="maxLines"/> lines and at most <paramref name="maxChars"/> characters -
	/// whichever binds first. The cut lands after a whole line's terminator, so a CRLF stays intact. The
	/// caller has already validated <paramref name="startLine"/> and <paramref name="maxLines"/>, so the
	/// window is never empty here.
	/// </summary>
	/// <returns>
	/// The page, the whole text's line count, whether any text follows the page, the 1-based line a
	/// continuation would start at, and the 1-based number of a line delivered partially (0 when none was).
	/// A line longer than <paramref name="maxChars"/> cannot be delivered whole by any page: as much of it
	/// as fits is delivered and named via <c>PartialLine</c> - the rest of that line is not retrievable by
	/// paging.
	/// </returns>
	public static (string Page, int TotalLines, bool More, int NextStartLine, int PartialLine) Slice(
		string text,
		int startLine,
		int maxLines,
		int maxChars)
	{
		int totalLines = CountLines(text);
		int offset = OffsetOfLine(text, startLine);
		int ceiling = Math.Max(1, maxChars);
		int budget = ceiling;
		int lines = 0;
		int pos = offset;
		int end = offset;
		int partial = 0;
		while (lines < maxLines)
		{
			int newline = text.IndexOf('\n', pos);
			int lineEnd = newline >= 0 ? newline + 1 : text.Length;
			int lineLength = lineEnd - pos;
			if (lineLength > budget)
			{
				if (lines == 0 && lineLength > ceiling)
				{
					// An indivisible line: longer than a whole page's char ceiling, so no page could ever
					// show it whole. Deliver as much as fits and name the line.
					int take = ResponseBudget.SafeCut(text, pos, budget);
					end = pos + take;
					lines = 1;
					partial = startLine;
				}

				break;
			}

			budget -= lineLength;
			lines++;
			end = lineEnd;
			if (newline < 0)
				break;
			pos = newline + 1;
		}

		bool more = end < text.Length;
		return (text[offset..end], totalLines, more, startLine + lines, partial);
	}

	/// <summary>The offset just past the ('\n'-terminated) line <paramref name="lineNumber"/> - 1 lines before it.</summary>
	private static int OffsetOfLine(string text, int lineNumber)
	{
		int offset = 0;
		for (int line = 1; line < lineNumber; line++)
		{
			int newline = text.IndexOf('\n', offset);
			if (newline < 0)
				return text.Length;
			offset = newline + 1;
		}

		return offset;
	}
}
