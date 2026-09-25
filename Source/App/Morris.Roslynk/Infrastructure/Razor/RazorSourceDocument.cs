using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// The C# document a tool operates on for a user-supplied path. For a <c>.cs</c> file it is that document.
/// For a <c>.razor</c>/<c>.cshtml</c> file it is the Razor-generated C# document, together with the Razor
/// source text, so line/column positions in the Razor file can be mapped to the generated code Roslyn
/// analyses. Edits Roslyn makes to the generated document are mapped back with
/// <see cref="RazorGeneratedChangeFolder"/>.
/// </summary>
public sealed class RazorSourceDocument
{
	/// <summary>The document Roslyn operates on: the .cs file itself, or the Razor-generated C#.</summary>
	public Document Document { get; }

	/// <summary>The Razor source path, or null for an ordinary .cs document.</summary>
	public string? RazorPath { get; }

	/// <summary>The Razor source text, or null for an ordinary .cs document.</summary>
	public SourceText? RazorText { get; }

	public bool IsRazor => RazorPath is not null;

	private RazorSourceDocument(Document document, string? razorPath, SourceText? razorText)
	{
		Document = document;
		RazorPath = razorPath;
		RazorText = razorText;
	}

	public static bool IsRazorSourcePath(string path) =>
		path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Resolves <paramref name="path"/> (absolute or solution-relative) to a .cs document, or to the generated
	/// document of a .razor/.cshtml file. Returns null when the path names no compiled document.
	/// </summary>
	public static async Task<RazorSourceDocument?> ResolveAsync(Solution solution, string path, CancellationToken cancellationToken = default)
	{
		if (!IsRazorSourcePath(path))
		{
			Document? document = CodeActionService.FindDocument(solution, path);
			return document is null ? null : new RazorSourceDocument(document, null, null);
		}

		TextDocument? razor = FindByPath(solution.Projects.SelectMany(project => project.AdditionalDocuments), solution, path);
		if (razor?.FilePath is not string razorPath)
			return null;

		Document? generated = await FindGeneratedDocumentAsync(solution.GetProject(razor.Project.Id)!, razorPath, cancellationToken);
		if (generated is null)
			return null;

		return new RazorSourceDocument(generated, razorPath, await razor.GetTextAsync(cancellationToken));
	}

	/// <summary>The text positions are expressed against: the Razor source, or the .cs document's text.</summary>
	public async Task<SourceText> GetSourceTextAsync(CancellationToken cancellationToken = default) =>
		RazorText ?? await Document.GetTextAsync(cancellationToken);

	/// <summary>
	/// Maps a span in the source text (see <see cref="GetSourceTextAsync"/>) to the equivalent span in
	/// <see cref="Document"/>. Identity for a .cs document; for Razor, both ends must fall inside C# the
	/// compiler copied from the Razor file (an <c>@code</c> block, an expression, a directive), or null.
	/// </summary>
	public async Task<TextSpan?> MapToDocumentAsync(TextSpan sourceSpan, CancellationToken cancellationToken = default)
	{
		if (RazorPath is null || RazorText is null)
			return sourceSpan;

		SyntaxTree? tree = await Document.GetSyntaxTreeAsync(cancellationToken);
		if (tree is null)
			return null;
		SourceText generatedText = await tree.GetTextAsync(cancellationToken);

		int? start = MapPosition(tree, generatedText, RazorPath, RazorText, sourceSpan.Start, cancellationToken);
		if (start is null)
			return null;
		if (sourceSpan.IsEmpty)
			return new TextSpan(start.Value, 0);

		int? end = MapPosition(tree, generatedText, RazorPath, RazorText, sourceSpan.End, cancellationToken);
		if (end is null || end.Value < start.Value)
			return null;

		return TextSpan.FromBounds(start.Value, end.Value);
	}

	/// <summary>True when <paramref name="location"/> in the generated document maps into this Razor file.</summary>
	public bool MapsToSource(Location location)
	{
		if (RazorPath is null)
			return true;

		FileLinePositionSpan mapped = location.GetMappedLineSpan();
		return mapped.HasMappedPath && MapsToSource(mapped.Path);
	}

	/// <summary>True when a #line-mapped <paramref name="path"/> is this Razor file (always true for a .cs document).</summary>
	public bool MapsToSource(string path) => RazorPath is null || SamePath(path, RazorPath);

	/// <summary>
	/// The Razor-generated document for <paramref name="razorPath"/>: the one the compiler named after it,
	/// falling back to any generated document whose #line directives map into it. Imports files such as
	/// <c>_Imports.razor</c> are mapped into every component, so the name match is preferred.
	/// </summary>
	private static async Task<Document?> FindGeneratedDocumentAsync(Project project, string razorPath, CancellationToken cancellationToken)
	{
		string expectedSuffix = Path.GetFileNameWithoutExtension(razorPath) + "_" + Path.GetExtension(razorPath).TrimStart('.') + ".g.cs";
		Document? fallback = null;

		foreach (Document document in project.Documents)
		{
			if (document.FilePath is not string path || !RazorMapping.IsRazorGeneratedPath(path))
				continue;

			SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
			if (tree is null || !tree.GetLineMappings(cancellationToken).Any(mapping => !mapping.IsHidden && SamePath(mapping.MappedSpan.Path, razorPath)))
				continue;

			if (path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
				return document;
			fallback ??= document;
		}

		return fallback;
	}

	/// <summary>
	/// The generated offset whose mapped position is <paramref name="razorOffset"/>, found through the #line
	/// mapping that covers it. The compiler copies C# regions verbatim, so the offset within the mapped region
	/// carries over; each candidate is confirmed by mapping it forward again.
	/// </summary>
	private static int? MapPosition(SyntaxTree tree, SourceText generatedText, string razorPath, SourceText razorText, int razorOffset, CancellationToken cancellationToken)
	{
		LinePosition target = razorText.Lines.GetLinePosition(razorOffset);

		foreach (LineMapping mapping in tree.GetLineMappings(cancellationToken))
		{
			if (mapping.IsHidden || !SamePath(mapping.MappedSpan.Path, razorPath))
				continue;
			if (!TryGetOffset(razorText, mapping.MappedSpan.StartLinePosition, out int razorStart)
				|| !TryGetOffset(razorText, mapping.MappedSpan.EndLinePosition, out int razorEnd)
				|| razorOffset < razorStart || razorOffset > razorEnd)
				continue;

			LinePosition generatedStart = mapping.CharacterOffset is int characterOffset
				? new LinePosition(mapping.Span.Start.Line, characterOffset)
				: mapping.Span.Start;
			if (!TryGetOffset(generatedText, generatedStart, out int generatedStartOffset))
				continue;

			int candidate = generatedStartOffset + (razorOffset - razorStart);
			if (candidate > generatedText.Length)
				continue;

			FileLinePositionSpan roundTrip = tree.GetMappedLineSpan(new TextSpan(candidate, 0), cancellationToken);
			if (roundTrip.HasMappedPath && SamePath(roundTrip.Path, razorPath) && roundTrip.StartLinePosition == target)
				return candidate;
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

	private static bool SamePath(string left, string right) =>
		string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

	private static string Normalize(string path) =>
		path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

	private static T? FindByPath<T>(IEnumerable<T> documents, Solution solution, string path) where T : TextDocument
	{
		string normalized = Normalize(path);
		string full = SolutionRelativePath.ToAbsolute(SolutionRelativePath.DirectoryOf(solution), normalized);

		T? suffixMatch = null;
		int suffixMatches = 0;
		foreach (T document in documents)
		{
			if (document.FilePath is null)
				continue;
			if (string.Equals(document.FilePath, full, StringComparison.OrdinalIgnoreCase))
				return document;
			if (document.FilePath.EndsWith(Path.DirectorySeparatorChar + normalized, StringComparison.OrdinalIgnoreCase))
			{
				// Multi-targeted projects list the same file once per target framework; count files, not copies.
				if (suffixMatch?.FilePath is null || !string.Equals(suffixMatch.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
					suffixMatches++;
				suffixMatch ??= document;
			}
		}

		return suffixMatches == 1 ? suffixMatch : null;
	}
}
