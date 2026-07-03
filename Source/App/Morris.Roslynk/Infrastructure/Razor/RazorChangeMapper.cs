using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Maps text changes computed against a Razor-generated <c>.g.cs</c> document back to equivalent changes
/// in the <c>.razor</c>/<c>.cshtml</c> source it was generated from, via the enhanced <c>#line</c>
/// directives the Razor compiler emits. Regions the compiler copies verbatim (<c>@code</c> blocks, inline
/// expressions, <c>nameof()</c> component-attribute names) map token-precisely, so a rename edit in the
/// generated document lands exactly on the identifier in the razor source. Every change is verified
/// against the razor text before it is emitted; a change that cannot be mapped and verified raises
/// <see cref="RazorMappingException"/> so the caller aborts without applying a partial edit.
/// </summary>
public static class RazorChangeMapper
{
	/// <summary>
	/// Maps <paramref name="changes"/> (spans in <paramref name="generatedOriginal"/>'s pre-edit
	/// coordinates, as returned by <c>GetTextChangesAsync</c>) to changes against the razor source
	/// file(s) named by the #line mapping. A single generated document can map to several razor files
	/// (e.g. usings inlined from <c>_Imports.razor</c>), so each result carries its target path.
	/// </summary>
	public static async Task<IReadOnlyList<(string RazorPath, TextChange Change)>> MapChangesAsync(
		Document generatedOriginal,
		IReadOnlyList<TextChange> changes,
		Func<string, Task<SourceText?>> razorTextProvider,
		CancellationToken cancellationToken = default)
	{
		if (generatedOriginal is null)
			throw new ArgumentNullException(nameof(generatedOriginal));
		if (changes is null)
			throw new ArgumentNullException(nameof(changes));
		if (razorTextProvider is null)
			throw new ArgumentNullException(nameof(razorTextProvider));

		string generatedPath = generatedOriginal.FilePath ?? generatedOriginal.Name;
		SyntaxTree syntaxTree = await generatedOriginal.GetSyntaxTreeAsync(cancellationToken)
			?? throw new RazorMappingException(RazorMappingFailure.Unmappable, generatedPath, $"'{generatedPath}' has no syntax tree to map through.");
		SourceText generatedText = await generatedOriginal.GetTextAsync(cancellationToken);

		var mapped = new List<(string RazorPath, TextChange Change)>(changes.Count);
		var razorTexts = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);

		foreach (TextChange change in changes)
		{
			cancellationToken.ThrowIfCancellationRequested();

			FileLinePositionSpan mappedSpan = syntaxTree.GetMappedLineSpan(change.Span, cancellationToken);
			if (!mappedSpan.HasMappedPath || !IsRazorSourcePath(mappedSpan.Path))
				throw new RazorMappingException(
					RazorMappingFailure.Unmappable,
					generatedPath,
					$"A change in '{generatedPath}' has no source mapping back to a .razor/.cshtml file; the edit was not applied.");

			if (!razorTexts.TryGetValue(mappedSpan.Path, out SourceText? razorText))
			{
				razorText = await razorTextProvider(mappedSpan.Path);
				razorTexts[mappedSpan.Path] = razorText;
			}

			if (razorText is null)
				throw new RazorMappingException(
					RazorMappingFailure.MissingSource,
					generatedPath,
					$"'{mappedSpan.Path}' is not loaded in the workspace as an additional document, so the edit cannot be applied to it; reload the solution (or build the project once) and retry.");

			TextSpan razorSpan = ToTextSpan(razorText, mappedSpan, generatedPath);

			string expected = generatedText.ToString(change.Span);
			string actual = razorText.ToString(razorSpan);
			if (!string.Equals(expected, actual, StringComparison.Ordinal))
				throw new RazorMappingException(
					RazorMappingFailure.TextMismatch,
					generatedPath,
					$"The mapped location {Display(mappedSpan)} contains '{actual}' where '{expected}' was expected; the generated documents may be stale — rebuild or reload the solution.");

			mapped.Add((mappedSpan.Path, new TextChange(razorSpan, change.NewText ?? "")));
		}

		return mapped;
	}

	private static bool IsRazorSourcePath(string path) =>
		path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	/// <summary>Line/column to absolute offsets, bounds-checked so a stale mapping fails cleanly rather than throwing out-of-range.</summary>
	private static TextSpan ToTextSpan(SourceText razorText, FileLinePositionSpan mappedSpan, string generatedPath)
	{
		if (!TryGetPosition(razorText, mappedSpan.StartLinePosition, out int start)
			|| !TryGetPosition(razorText, mappedSpan.EndLinePosition, out int end)
			|| end < start)
			throw new RazorMappingException(
				RazorMappingFailure.TextMismatch,
				generatedPath,
				$"The mapped location {Display(mappedSpan)} is outside the file's current text; the generated documents may be stale — rebuild or reload the solution.");

		return TextSpan.FromBounds(start, end);
	}

	private static bool TryGetPosition(SourceText text, LinePosition position, out int offset)
	{
		offset = 0;
		if (position.Line < 0 || position.Line >= text.Lines.Count)
			return false;

		TextLine line = text.Lines[position.Line];
		offset = line.Start + position.Character;
		return position.Character >= 0 && offset <= line.EndIncludingLineBreak;
	}

	private static string Display(FileLinePositionSpan mappedSpan) =>
		$"{mappedSpan.Path}({mappedSpan.StartLinePosition.Line + 1},{mappedSpan.StartLinePosition.Character + 1})";
}
