using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Folds edits that a Roslyn operation made to Razor-generated <c>.g.cs</c> documents back onto the
/// <c>.razor</c>/<c>.cshtml</c> additional documents they were generated from. Generated documents are never
/// persisted, so without this an edit to a member declared in a <c>@code</c> block (or a call site in markup)
/// would be published in memory but silently dropped on disk.
/// </summary>
/// <remarks>
/// The fold works region by region rather than edit by edit. The Razor compiler copies each C# region of the
/// Razor file verbatim between a <c>#line</c> directive and the next <c>#line default</c>; Roslyn operations
/// that format their result (extract method, code fixes) re-indent to the generated class's nesting and can
/// move whitespace across the directives, so their raw text edits do not map. Instead each region of the
/// original generated document is paired with the same region of the updated one, everything outside the
/// regions must be unchanged apart from whitespace, and each changed region is written back to its Razor span
/// line by line: lines Roslyn left alone keep the user's text, and lines it formatted are re-based from the
/// generated class's indentation onto the file's own. Each changed generated region is then replaced in memory
/// by the reconciled Razor text, so the generated documents stay a verbatim image of the rewritten sources and a
/// following operation maps correctly without re-running the Razor generator.
/// </remarks>
public static class RazorGeneratedChangeFolder
{
	/// <summary>The indentation width Roslyn formats generated code with.</summary>
	private const int GeneratedIndentSize = 4;

	/// <summary>The public error for a failed mapping: a mismatch with the Razor text is a Conflict, anything else NotSupported.</summary>
	public static Error ErrorFor(RazorMappingException exception) =>
		exception.Kind == RazorMappingFailure.TextMismatch
			? Error.Conflict(exception.Message)
			: Error.NotSupported(exception.Message);

	/// <summary>
	/// Returns <paramref name="updated"/> with every Razor-generated change also applied to its Razor source.
	/// Throws <see cref="RazorMappingException"/> when any change cannot be mapped and verified, so the caller
	/// aborts without a partial edit.
	/// </summary>
	public static async Task<Solution> FoldAsync(Solution original, Solution updated, CancellationToken cancellationToken = default)
	{
		var razorChangesByPath = new Dictionary<string, Dictionary<TextSpan, TextChange>>(StringComparer.OrdinalIgnoreCase);
		var foldedGeneratedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (ProjectChanges projectChanges in updated.GetChanges(original).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				Document originalDocument = original.GetDocument(documentId)!;
				// Every copy of a generated file (one per target framework) carries the same edit; fold it once.
				if (originalDocument.FilePath is not string path || !RazorMapping.IsRazorGeneratedPath(path) || !foldedGeneratedPaths.Add(path))
					continue;

				Document updatedDocument = updated.GetDocument(documentId)!;
				(IReadOnlyList<(string RazorPath, TextChange Change)> razorChanges, SourceText generatedText) =
					await FoldDocumentAsync(original, originalDocument, updatedDocument, cancellationToken);

				// Keep each generated copy a verbatim image of its Razor source, so the next operation's
				// positions and mapping still line up without re-running the generator.
				foreach (DocumentId copyId in updated.GetDocumentIdsWithFilePath(path))
				{
					if (updated.GetDocument(copyId) is not null)
						updated = updated.WithDocumentText(copyId, generatedText);
				}

				foreach ((string razorPath, TextChange change) in razorChanges)
				{
					if (!razorChangesByPath.TryGetValue(razorPath, out Dictionary<TextSpan, TextChange>? perSpan))
					{
						perSpan = [];
						razorChangesByPath[razorPath] = perSpan;
					}

					if (perSpan.TryGetValue(change.Span, out TextChange existing) && !string.Equals(existing.NewText, change.NewText, StringComparison.Ordinal))
						throw new RazorMappingException(
							RazorMappingFailure.TextMismatch,
							path,
							$"Conflicting edits mapped to the same location in '{razorPath}'; nothing was applied.");

					perSpan[change.Span] = change;
				}
			}
		}

		foreach ((string razorPath, Dictionary<TextSpan, TextChange> perSpan) in razorChangesByPath)
		{
			List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ThenBy(change => change.Span.End).ToList();
			for (int index = 1; index < ordered.Count; index++)
			{
				if (ordered[index].Span.Start < ordered[index - 1].Span.End)
					throw new RazorMappingException(
						RazorMappingFailure.Unmappable,
						razorPath,
						$"The edits mapped to overlapping locations in '{razorPath}'; nothing was applied.");
			}

			foreach (DocumentId documentId in original.GetDocumentIdsWithFilePath(razorPath))
			{
				if (original.GetAdditionalDocument(documentId) is not TextDocument additional)
					continue;

				SourceText text = await additional.GetTextAsync(cancellationToken);
				updated = updated.WithAdditionalDocumentText(documentId, text.WithChanges(ordered));
			}
		}

		return updated;
	}

	/// <summary>
	/// The Razor-source changes equivalent to the edit from <paramref name="before"/> to <paramref name="after"/>,
	/// and the updated generated text with each changed region replaced by its reconciled Razor text.
	/// </summary>
	private static async Task<(IReadOnlyList<(string RazorPath, TextChange Change)> Changes, SourceText GeneratedText)> FoldDocumentAsync(
		Solution original,
		Document before,
		Document after,
		CancellationToken cancellationToken)
	{
		string generatedPath = before.FilePath ?? before.Name;
		SyntaxTree beforeTree = await before.GetSyntaxTreeAsync(cancellationToken)
			?? throw new RazorMappingException(RazorMappingFailure.Unmappable, generatedPath, $"'{generatedPath}' has no syntax tree to map through.");
		SyntaxTree afterTree = await after.GetSyntaxTreeAsync(cancellationToken)
			?? throw new RazorMappingException(RazorMappingFailure.Unmappable, generatedPath, $"'{generatedPath}' has no syntax tree to map through.");
		SourceText beforeText = await beforeTree.GetTextAsync(cancellationToken);
		SourceText afterText = await afterTree.GetTextAsync(cancellationToken);

		IReadOnlyList<Region> beforeRegions = Regions(beforeTree, beforeText, cancellationToken);
		IReadOnlyList<Region> afterRegions = Regions(afterTree, afterText, cancellationToken);
		if (beforeRegions.Count != afterRegions.Count
			|| beforeRegions.Zip(afterRegions).Any(pair => !pair.First.SameDirective(pair.Second)))
		{
			throw new RazorMappingException(
				RazorMappingFailure.Unmappable,
				generatedPath,
				$"The change to '{generatedPath}' added, removed or moved generated #line regions, so it cannot be mapped back to the Razor source; nothing was applied.");
		}

		if (!string.Equals(Outside(beforeText, beforeRegions), Outside(afterText, afterRegions), StringComparison.Ordinal))
		{
			throw new RazorMappingException(
				RazorMappingFailure.Unmappable,
				generatedPath,
				$"The change edits generated code outside the C# copied from the Razor source (for example a using or member added to the generated class), so it cannot be applied to the .razor/.cshtml file; nothing was applied.");
		}

		var razorTexts = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);
		var mapped = new List<(string RazorPath, TextChange Change)>();
		var generatedChanges = new List<TextChange>();
		for (int index = 0; index < beforeRegions.Count; index++)
		{
			Region beforeRegion = beforeRegions[index];
			Region afterRegion = afterRegions[index];
			string beforeContent = beforeText.ToString(beforeRegion.Span);
			string afterContent = afterText.ToString(afterRegion.Span);
			if (string.Equals(beforeContent, afterContent, StringComparison.Ordinal))
				continue;

			if (!razorTexts.TryGetValue(beforeRegion.RazorPath, out SourceText? razorText))
			{
				razorText = await RazorTextAsync(original, beforeRegion.RazorPath, cancellationToken);
				razorTexts[beforeRegion.RazorPath] = razorText;
			}
			if (razorText is null)
				throw new RazorMappingException(
					RazorMappingFailure.MissingSource,
					generatedPath,
					$"'{beforeRegion.RazorPath}' is not loaded in the workspace as an additional document, so the edit cannot be applied to it; reload the solution (or build the project once) and retry.");

			if (!TryGetOffset(razorText, beforeRegion.RazorStart, out int razorStart))
				throw Stale(generatedPath, beforeRegion);

			// The compiler copies the region verbatim and then appends its own line breaks, so the region's Razor
			// text is exactly the prefix the generated region shares with the Razor file at the mapped start.
			int length = CommonPrefixLength(beforeContent, razorText, razorStart);
			string tail = beforeContent[length..];
			if (length == 0 && beforeContent.Length > 0 && !string.IsNullOrWhiteSpace(beforeContent))
				throw Stale(generatedPath, beforeRegion);
			if (!string.IsNullOrWhiteSpace(tail) && !afterContent.EndsWith(tail, StringComparison.Ordinal))
				throw new RazorMappingException(
					RazorMappingFailure.Unmappable,
					generatedPath,
					$"The change edits generated code next to the C# copied from '{beforeRegion.RazorPath}', so it cannot be applied to it; nothing was applied.");

			string oldRazor = beforeContent[..length];
			string newRazor = afterContent.EndsWith(tail, StringComparison.Ordinal)
				? afterContent[..^tail.Length]
				: afterContent.TrimEnd() + oldRazor[oldRazor.TrimEnd().Length..];

			string reconciled = Reconcile(oldRazor, newRazor, NewLineOf(razorText));			if (!string.Equals(reconciled, oldRazor, StringComparison.Ordinal))
				mapped.Add((beforeRegion.RazorPath, new TextChange(new TextSpan(razorStart, length), reconciled)));
			generatedChanges.Add(new TextChange(afterRegion.Span, reconciled + tail));
		}

		return (mapped, afterText.WithChanges(generatedChanges));
	}

	/// <summary>
	/// Rewrites <paramref name="oldText"/> (the Razor file's region) to carry <paramref name="newText"/>'s edits,
	/// in the file's own indentation. The new text is taken line by line: lines Roslyn left alone are still the
	/// user's verbatim text, while lines it formatted are indented for the generated class's nesting and are
	/// re-based onto the file's indentation.
	/// </summary>
	private static string Reconcile(string oldText, string newText, string newLine)
	{
		List<Line> oldLines = SplitLines(oldText);
		List<Line> newLines = SplitLines(newText);
		string unit = IndentUnitOf(oldLines);
		bool[] formatted = newLines.Select(line => IsFormatted(line, unit)).ToArray();
		int offset = FormattingOffset(oldLines, newLines, formatted, unit);

		var builder = new StringBuilder();
		for (int index = 0; index < newLines.Count; index++)
		{
			Line line = newLines[index];
			// The last line's break is the compiler's or Roslyn's to decide; follow the new text there.
			string ending = line.Ending.Length == 0 ? "" : newLine;
			if (line.Content.Length == 0)
				builder.Append(ending);
			else if (!formatted[index])
				builder.Append(line.Indent).Append(line.Content).Append(ending);
			else
				builder.Append(IndentOf(Math.Max(0, (Columns(line.Indent) - offset) / GeneratedIndentSize), unit)).Append(line.Content).Append(ending);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Whether Roslyn formatted this line. Roslyn indents generated code with spaces, so in a tab-indented file a
	/// space-indented line is Roslyn's; in a space-indented file every indented line is treated as formatted and
	/// re-based (an offset of zero leaves the user's lines as they were).
	/// </summary>
	private static bool IsFormatted(Line line, string unit) =>
		line.Content.Length > 0 && (unit != "\t" || line.Indent.Contains(' '));

	/// <summary>
	/// The generated columns that precede the file's indentation level zero in Roslyn-formatted lines: the most
	/// common difference, over formatted lines whose text occurs exactly once in the original region, between
	/// Roslyn's columns and the original line's level. Falls back to aligning the shallowest formatted line with
	/// the shallowest original line.
	/// </summary>
	private static int FormattingOffset(List<Line> oldLines, List<Line> newLines, bool[] formatted, string unit)
	{
		Dictionary<string, Line> unique = oldLines
			.Where(line => line.Content.Length > 0)
			.GroupBy(line => line.Content, StringComparer.Ordinal)
			.Where(group => group.Count() == 1)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

		var votes = new Dictionary<int, int>();
		for (int index = 0; index < newLines.Count; index++)
		{
			if (formatted[index] && unique.TryGetValue(newLines[index].Content, out Line? original))
			{
				int offset = Columns(newLines[index].Indent) - Level(original.Indent, unit) * GeneratedIndentSize;
				votes[offset] = votes.GetValueOrDefault(offset) + 1;
			}
		}

		if (votes.Count > 0)
			return votes.OrderByDescending(vote => vote.Value).ThenBy(vote => vote.Key).First().Key;

		int[] formattedColumns = newLines.Where((_, index) => formatted[index]).Select(line => Columns(line.Indent)).ToArray();
		int[] originalLevels = oldLines.Where(line => line.Content.Length > 0).Select(line => Level(line.Indent, unit)).ToArray();
		return formattedColumns.Length == 0 || originalLevels.Length == 0
			? 0
			: formattedColumns.Min() - originalLevels.Min() * GeneratedIndentSize;
	}

	/// <summary>The file's indentation unit: a tab if the region indents with tabs, otherwise four spaces.</summary>
	private static string IndentUnitOf(List<Line> lines) =>
		lines.Any(line => line.Indent.Contains('\t')) || !lines.Any(line => line.Indent.Length > 0) ? "\t" : "    ";

	private static int Level(string indent, string unit) =>
		unit == "\t" ? indent.Count(character => character == '\t') + indent.Count(character => character == ' ') / GeneratedIndentSize : Columns(indent) / GeneratedIndentSize;

	private static string IndentOf(int level, string unit) => string.Concat(Enumerable.Repeat(unit, level));

	private static int Columns(string indent) =>
		indent.Sum(character => character == '\t' ? GeneratedIndentSize : 1);

	private sealed record Line(string Indent, string Content, string Ending);

	private static List<Line> SplitLines(string text)
	{
		var lines = new List<Line>();
		int start = 0;
		while (start < text.Length)
		{
			int end = start;
			while (end < text.Length && text[end] != '\r' && text[end] != '\n')
				end++;

			int next = end;
			if (next < text.Length && text[next] == '\r')
				next++;
			if (next < text.Length && text[next] == '\n')
				next++;

			string body = text[start..end];
			string content = body.TrimStart(' ', '\t');
			lines.Add(new Line(body[..(body.Length - content.Length)], content.TrimEnd(' ', '\t'), text[end..next]));
			start = next;
		}

		return lines;
	}

	private static string NewLineOf(SourceText text) =>
		text.Lines.Count > 1 && text.Lines[0].EndIncludingLineBreak - text.Lines[0].End == 1 ? "\n" : "\r\n";

	private static int CommonPrefixLength(string generated, SourceText razorText, int razorStart)
	{
		int length = 0;
		while (length < generated.Length && razorStart + length < razorText.Length && generated[length] == razorText[razorStart + length])
			length++;
		return length;
	}

	/// <summary>The generated text outside every region, with all whitespace removed.</summary>
	private static string Outside(SourceText text, IReadOnlyList<Region> regions)
	{
		var builder = new StringBuilder();
		int position = 0;
		foreach (Region region in regions)
		{
			AppendNonWhitespace(builder, text, TextSpan.FromBounds(position, region.Span.Start));
			position = region.Span.End;
		}

		AppendNonWhitespace(builder, text, TextSpan.FromBounds(position, text.Length));
		return builder.ToString();
	}

	private static void AppendNonWhitespace(StringBuilder builder, SourceText text, TextSpan span)
	{
		for (int index = span.Start; index < span.End; index++)
		{
			if (!char.IsWhiteSpace(text[index]))
				builder.Append(text[index]);
		}
	}

	/// <summary>
	/// A C# region the compiler copied from a Razor file: from the start its <c>#line</c> directive maps (after
	/// any character offset) up to the next <c>#line</c> directive.
	/// </summary>
	private sealed record Region(string RazorPath, LinePositionSpan RazorSpan, int? CharacterOffset, TextSpan Span)
	{
		public LinePosition RazorStart => RazorSpan.Start;

		public bool SameDirective(Region other) =>
			string.Equals(RazorPath, other.RazorPath, StringComparison.OrdinalIgnoreCase) && RazorSpan == other.RazorSpan && CharacterOffset == other.CharacterOffset;
	}

	private static IReadOnlyList<Region> Regions(SyntaxTree tree, SourceText text, CancellationToken cancellationToken)
	{
		var regions = new List<Region>();
		foreach (LineMapping mapping in tree.GetLineMappings(cancellationToken))
		{
			if (mapping.IsHidden || !mapping.MappedSpan.HasMappedPath || !IsRazorSourcePath(mapping.MappedSpan.Path))
				continue;

			int line = mapping.Span.Start.Line;
			if (line >= text.Lines.Count)
				continue;

			int start = Math.Min(text.Lines[line].Start + (mapping.CharacterOffset ?? mapping.Span.Start.Character), text.Lines[line].End);
			int end = text.Length;
			for (int next = line + 1; next < text.Lines.Count; next++)
			{
				if (text.ToString(text.Lines[next].Span).TrimStart().StartsWith("#line", StringComparison.Ordinal))
				{
					end = text.Lines[next].Start;
					break;
				}
			}

			regions.Add(new Region(mapping.MappedSpan.Path, mapping.MappedSpan.Span, mapping.CharacterOffset, TextSpan.FromBounds(start, end)));
		}

		return regions;
	}

	private static bool IsRazorSourcePath(string path) =>
		path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	private static async Task<SourceText?> RazorTextAsync(Solution solution, string razorPath, CancellationToken cancellationToken)
	{
		foreach (DocumentId documentId in solution.GetDocumentIdsWithFilePath(razorPath))
		{
			if (solution.GetAdditionalDocument(documentId) is TextDocument additional)
				return await additional.GetTextAsync(cancellationToken);
		}

		return null;
	}

	private static bool TryGetOffset(SourceText text, LinePosition position, out int offset)
	{
		offset = 0;
		if (position.Line < 0 || position.Line >= text.Lines.Count || position.Character < 0)
			return false;

		TextLine line = text.Lines[position.Line];
		offset = line.Start + position.Character;
		return offset <= line.EndIncludingLineBreak;
	}

	private static RazorMappingException Stale(string generatedPath, Region region) =>
		new(
			RazorMappingFailure.TextMismatch,
			generatedPath,
			$"The C# mapped to '{region.RazorPath}'({region.RazorStart.Line + 1},{region.RazorStart.Character + 1}) no longer matches that file; the generated documents may be stale — rebuild or reload the solution.");
}
