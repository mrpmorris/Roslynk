namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Line counting for verbatim source text: lines are '\n'-delimited with each '\r' kept attached to its
/// own line, so CRLF and LF sources both count one line per break. (Page slicing itself lives in
/// <see cref="ItemPaging"/>; get_symbol_body's items are these lines.)
/// </summary>
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

}
