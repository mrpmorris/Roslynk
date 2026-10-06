using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Maps text changes computed against a Razor-generated <c>.g.cs</c> document back to equivalent changes
/// in the <c>.razor</c>/<c>.cshtml</c> source it was generated from, via the enhanced <c>#line</c>
/// directives the Razor compiler emits. Regions the compiler copies verbatim (<c>@code</c> blocks, inline
/// expressions, <c>nameof()</c> component-attribute names) map token-precisely, so a rename edit in the
/// generated document lands exactly on the identifier in the razor source.
/// <para>
/// A <c>@bind-X="Expr"</c> attribute compiles into several generated spans from one source span: the
/// attribute value itself (mapped), plus setter lambdas and a <c>ValueExpression</c> lambda inside
/// <c>#line hidden</c> scaffolding (unmapped) that repeat the same expression. An unmapped edit is
/// redundant when the mapped span it was expanded from already carries the identical edit, so it is dropped.
/// Every change is verified against the razor text before it is emitted; an unmapped change that cannot be
/// explained that way raises <see cref="RazorMappingException"/> so the caller aborts without applying a
/// partial edit.
/// </para>
/// </summary>
public static class RazorChangeMapper
{
	/// <summary>How many preceding mapped regions one <c>@bind</c> expansion can span (value, get, set, handler, after).</summary>
	private const int MaxExpansionRegions = 6;

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

		// The C# spans the compiler copied from razor files, in generated order. Generated scaffolding
		// between two of them (what a @bind attribute is expanded into) belongs to the region before it.
		var regions = new List<(int GeneratedStart, LineMapping Mapping)>();
		foreach (LineMapping mapping in syntaxTree.GetLineMappings(cancellationToken))
		{
			if (mapping.IsHidden || !mapping.MappedSpan.HasMappedPath || !IsRazorSourcePath(mapping.MappedSpan.Path))
				continue;
			if (mapping.Span.Start.Line < generatedText.Lines.Count)
			{
				int start = Math.Min(
					generatedText.Lines[mapping.Span.Start.Line].Start + (mapping.CharacterOffset ?? mapping.Span.Start.Character),
					generatedText.Lines[mapping.Span.Start.Line].End);
				regions.Add((start, mapping));
			}
		}

		regions.Sort((left, right) => left.GeneratedStart.CompareTo(right.GeneratedStart));

		var mapped = new List<(string RazorPath, TextChange Change)>(changes.Count);
		var unmapped = new List<TextChange>();
		var razorTexts = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);

		foreach (TextChange original in changes)
		{
			cancellationToken.ThrowIfCancellationRequested();

			TextChange change = Trim(generatedText, original);

			FileLinePositionSpan mappedSpan = syntaxTree.GetMappedLineSpan(change.Span, cancellationToken);
			if (!mappedSpan.HasMappedPath || !IsRazorSourcePath(mappedSpan.Path))
			{
				unmapped.Add(change);
				continue;
			}

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

		foreach (TextChange change in unmapped)
			await EnsureCoveredByMappedEditAsync(generatedPath, generatedText, change, regions, mapped, razorTexts, razorTextProvider, cancellationToken);

		return mapped;
	}

	/// <summary>
	/// Accepts a change that landed in generated scaffolding with no #line mapping of its own only when it is
	/// a copy of an edit already mapped. A <c>@bind-X</c> attribute is expanded from its source span into a
	/// mapped value (and handler) span plus hidden scaffolding — setter lambdas and a <c>ValueExpression</c>
	/// lambda — that repeats the same expression, so a rename edit there duplicates the edit the semantic
	/// rename also made inside the mapped span. At most <see cref="MaxExpansionRegions"/> regions are scanned
	/// backwards from the edit (an expansion has a value, get/set, handler and after span at most); the first
	/// one carrying a mapped edit with the same old and new text explains the change, which is then dropped
	/// (it collapses into the user-written edit). Dropping can never lose a razor edit, because every
	/// occurrence in razor source is itself mapped. Anything else — a class declaration, a generated member —
	/// cannot be explained and aborts the whole operation.
	/// </summary>
	private static async Task EnsureCoveredByMappedEditAsync(
		string generatedPath,
		SourceText generatedText,
		TextChange change,
		List<(int GeneratedStart, LineMapping Mapping)> regions,
		List<(string RazorPath, TextChange Change)> mapped,
		Dictionary<string, SourceText?> razorTexts,
		Func<string, Task<SourceText?>> razorTextProvider,
		CancellationToken cancellationToken)
	{
		string oldText = generatedText.ToString(change.Span);
		string newText = change.NewText ?? "";

		int owner = regions.FindLastIndex(region => region.GeneratedStart <= change.Span.Start);
		for (int index = owner; index >= 0 && owner - index < MaxExpansionRegions; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();

			string razorPath = regions[index].Mapping.MappedSpan.Path;
			if (!razorTexts.TryGetValue(razorPath, out SourceText? razorText))
			{
				razorText = await razorTextProvider(razorPath);
				razorTexts[razorPath] = razorText;
			}

			if (razorText is null)
				throw new RazorMappingException(
					RazorMappingFailure.MissingSource,
					generatedPath,
					$"'{razorPath}' is not loaded in the workspace as an additional document, so the edit cannot be applied to it; reload the solution (or build the project once) and retry.");

			TextSpan regionSpan = ToTextSpan(razorText, regions[index].Mapping.MappedSpan, generatedPath);

			foreach ((string mappedPath, TextChange mappedChange) in mapped)
			{
				if (string.Equals(mappedPath, razorPath, StringComparison.OrdinalIgnoreCase)
					&& regionSpan.Contains(mappedChange.Span)
					&& string.Equals(mappedChange.NewText, newText, StringComparison.Ordinal)
					&& string.Equals(razorText.ToString(mappedChange.Span), oldText, StringComparison.Ordinal))
					return;
			}
		}

		throw new RazorMappingException(
			RazorMappingFailure.Unmappable,
			generatedPath,
			$"A change to '{oldText}' in '{generatedPath}' lies in generated scaffolding that no .razor/.cshtml edit accounts for; the edit was not applied.");
	}

	/// <summary>
	/// Narrows a change to the text that actually differs by dropping the prefix and suffix its old and new
	/// text share. Text diffs can sweep unchanged neighbouring lines into one change, and those lines may lie
	/// in generated scaffolding with no Razor mapping; the narrowed change is equivalent but maps cleanly.
	/// </summary>
	private static TextChange Trim(SourceText generatedText, TextChange change)
	{
		string oldText = generatedText.ToString(change.Span);
		string newText = change.NewText ?? "";

		int prefix = 0;
		int maxPrefix = Math.Min(oldText.Length, newText.Length);
		while (prefix < maxPrefix && oldText[prefix] == newText[prefix])
			prefix++;

		int suffix = 0;
		int maxSuffix = Math.Min(oldText.Length, newText.Length) - prefix;
		while (suffix < maxSuffix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
			suffix++;

		if (prefix == 0 && suffix == 0)
			return change;

		var span = TextSpan.FromBounds(change.Span.Start + prefix, change.Span.End - suffix);
		return new TextChange(span, newText.Substring(prefix, newText.Length - prefix - suffix));
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
