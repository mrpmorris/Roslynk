using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>A contiguous range of Razor source text that a generated declaration maps onto, with the
/// project that loads the .razor/.cshtml file as an additional document.</summary>
public sealed record RazorSourceRange(string Path, FileLinePositionSpan Span, string Text, Project? Project);

public static class RazorMapping
{
	public static bool IsRazorGeneratedDocument(SyntaxTree syntaxTree)
	{
		if (syntaxTree?.FilePath is not string path)
			return false;
		return IsRazorGeneratedPath(path);
	}

	public static bool IsRazorGeneratedDocument(Document document)
	{
		if (document?.FilePath is not string path)
			return false;
		return IsRazorGeneratedPath(path);
	}

	public static bool IsRazorGeneratedPath(string filePath)
	{
		if (!filePath.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase)
			&& !filePath.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase))
			return false;

		// Pre-generated files loaded from a prior dotnet build (under the generator's own folder, wherever
		// CompilerGeneratedFilesOutputPath put it), or documents produced by the in-process generator run
		// (RazorDocumentGenerator's RoslynkRazorGenerated folder). Generated paths can mix separators.
		string normalized = filePath.Replace('\\', '/');
		return normalized.Contains("/Microsoft.CodeAnalysis.Razor.Compiler/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/RoslynkRazorGenerated/", StringComparison.OrdinalIgnoreCase);
	}

	public static FileLinePositionSpan GetDisplaySpan(this Location location)
	{
		FileLinePositionSpan mapped = location.GetMappedLineSpan();
		if (!string.IsNullOrEmpty(mapped.Path))
			return mapped;
		return location.GetLineSpan();
	}

	public static FileLinePositionSpan GetDisplaySpan(this SyntaxTree syntaxTree, TextSpan textSpan)
	{
		FileLinePositionSpan mapped = syntaxTree.GetMappedLineSpan(textSpan);
		if (!string.IsNullOrEmpty(mapped.Path))
			return mapped;
		return syntaxTree.GetLineSpan(textSpan);
	}

	/// <summary>
	/// Maps a span of a Razor-generated document onto the .razor/.cshtml source it came from. A span that
	/// crosses <c>#line</c> region boundaries (a whole <c>@inject</c> property declaration, the
	/// <c>BuildRenderTree</c> body) has no single mapped span, so <see cref="GetDisplaySpan"/> cannot map
	/// it; this method splits it into its mapped regions instead.
	/// <para>
	/// Each mapped region is widened to the whole Razor line(s) it covers - a <c>@inject</c> directive
	/// maps only the type and name tokens, so the caller wants the directive line, not those tokens - and
	/// adjacent or overlapping ranges of one file are merged. Generated scaffolding between regions (for
	/// example the setter lambdas of a <c>@bind-X</c> attribute, or a class declaration's <c>#line
	/// hidden</c> lines) maps to no Razor text and is skipped, so a declaration with no mapped region
	/// returns no ranges and keeps its generated text. Regions naming a file that is not loaded as an
	/// additional document are skipped too, so a stale mapping cannot invent a path.
	/// </para>
	/// </summary>
	public static async Task<IReadOnlyList<RazorSourceRange>> MapToRazorSourceAsync(
		Solution solution,
		SyntaxTree tree,
		TextSpan generatedSpan,
		SourceText generatedText,
		CancellationToken cancellationToken = default)
	{
		if (!IsRazorGeneratedDocument(tree))
			return [];

		var regions = new List<(int Start, int End, string Path, LinePosition StartPosition, LinePosition EndPosition)>();
		foreach (LineMapping mapping in tree.GetLineMappings(cancellationToken))
		{
			if (mapping.IsHidden || !mapping.MappedSpan.HasMappedPath || !IsRazorSourcePath(mapping.MappedSpan.Path))
				continue;
			if (!TryGeneratedRegion(generatedText, mapping, out TextSpan region))
				continue;
			if (region.End <= generatedSpan.Start || region.Start >= generatedSpan.End)
				continue;

			regions.Add((region.Start, region.End, mapping.MappedSpan.Path,
				mapping.MappedSpan.StartLinePosition, mapping.MappedSpan.EndLinePosition));
		}

		var ranges = new List<RazorSourceRange>();
		var texts = new Dictionary<string, (SourceText? Text, Project? Project)>(StringComparer.OrdinalIgnoreCase);
		foreach (IGrouping<string, (int Start, int End, string Path, LinePosition StartPosition, LinePosition EndPosition)> file in
			regions.GroupBy(region => region.Path, StringComparer.OrdinalIgnoreCase))
		{
			if (!texts.TryGetValue(file.Key, out (SourceText? Text, Project? Project) loaded))
			{
				loaded = await FindRazorTextAsync(solution, file.Key, cancellationToken);
				texts[file.Key] = loaded;
			}

			if (loaded.Text is not SourceText razorText)
				continue;

			foreach ((int startLine, int endLine) in MergeLines(razorText, file))
			{
				TextLine first = razorText.Lines[startLine];
				TextLine last = razorText.Lines[endLine];
				var span = new FileLinePositionSpan(
					file.Key,
					new LinePosition(startLine, 0),
					new LinePosition(endLine, last.End - last.Start));
				ranges.Add(new RazorSourceRange(
					file.Key,
					span,
					razorText.ToString(TextSpan.FromBounds(first.Start, last.End)),
					loaded.Project));
			}
		}

		return ranges;
	}

	/// <summary>
	/// The mapped regions' whole-line ranges in one Razor file, ordered and merged where they touch: a
	/// region that ends at column 0 of the next line ends with the previous line's content, and a range
	/// starting on the line after another's last line is contiguous (only the line break separates them).
	/// </summary>
	private static IReadOnlyList<(int StartLine, int EndLine)> MergeLines(
		SourceText razorText,
		IEnumerable<(int Start, int End, string Path, LinePosition StartPosition, LinePosition EndPosition)> regions)
	{
		var lines = new List<(int StartLine, int EndLine)>();
		foreach ((int _, int _, string _, LinePosition start, LinePosition end) in regions.OrderBy(region => region.Start))
		{
			int startLine = start.Line;
			int endLine = end.Line;
			if (end.Character == 0 && endLine > startLine)
				endLine--;
			if (startLine < 0 || startLine >= razorText.Lines.Count)
				continue;
			endLine = Math.Min(endLine, razorText.Lines.Count - 1);
			if (endLine < startLine)
				endLine = startLine;

			if (lines.Count > 0 && startLine <= lines[^1].EndLine + 1)
				lines[^1] = (lines[^1].StartLine, Math.Max(lines[^1].EndLine, endLine));
			else
				lines.Add((startLine, endLine));
		}

		return lines;
	}

	/// <summary>The generated document offsets a <c>#line</c> mapping covers, clamped so a stale mapping
	/// cannot index past the text.</summary>
	private static bool TryGeneratedRegion(SourceText generatedText, LineMapping mapping, out TextSpan region)
	{
		region = default;
		LinePosition start = mapping.Span.Start;
		LinePosition end = mapping.Span.End;
		if (start.Line < 0 || start.Line >= generatedText.Lines.Count)
			return false;

		TextLine first = generatedText.Lines[start.Line];
		int startOffset = Math.Min(first.Start + (mapping.CharacterOffset ?? start.Character), first.End);

		int endOffset;
		if (end.Line >= generatedText.Lines.Count)
		{
			endOffset = generatedText.Length;
		}
		else
		{
			TextLine last = generatedText.Lines[end.Line];
			endOffset = Math.Min(last.Start + end.Character, last.EndIncludingLineBreak);
		}

		if (endOffset < startOffset)
			return false;

		region = TextSpan.FromBounds(startOffset, endOffset);
		return true;
	}

	/// <summary>The .razor/.cshtml additional document at <paramref name="path"/>, with its project. A
	/// generated document can map into a different file than its own name (an <c>@inject</c> or
	/// <c>@using</c> from <c>_Imports.razor</c>), so the path decides.</summary>
	private static async Task<(SourceText? Text, Project? Project)> FindRazorTextAsync(
		Solution solution,
		string path,
		CancellationToken cancellationToken)
	{
		foreach (Project project in solution.Projects)
		{
			foreach (TextDocument document in project.AdditionalDocuments)
			{
				if (document.FilePath is not string candidate || !SamePath(candidate, path))
					continue;

				return (await document.GetTextAsync(cancellationToken), project);
			}
		}

		return (null, null);
	}

	private static bool SamePath(string left, string right)
	{
		string normalizedLeft = left.Replace('\\', '/');
		string normalizedRight = right.Replace('\\', '/');
		return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal)
			|| (OperatingSystem.IsWindows() && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase));
	}

	private static bool IsRazorSourcePath(string path) =>
		path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
}
