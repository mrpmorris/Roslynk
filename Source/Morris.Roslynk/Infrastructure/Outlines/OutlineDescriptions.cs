namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Recurring fragments shared by the tools' <c>[Description]</c> attributes, so the wording for the common
/// output shape (the kind vocabulary, the location format, the pipe-delimited list, the header/error block)
/// is defined once and interpolated into each attribute via a constant interpolated string rather than
/// copied. Every member is a compile-time <c>const</c> so it is legal inside an attribute argument.
/// </summary>
internal static class OutlineDescriptions
{
	/// <summary>The vocabulary a &lt;kind&gt; field can take.</summary>
	public const string KindList = "method|property|field|event|class|struct|interface|enum|delegate";

	/// <summary>How a single &lt;loc&gt; is written.</summary>
	public const string Loc = "a location is line:col, or startLine:startCol-endLine:endCol when it spans lines";

	/// <summary>How a list of locations is written inside one comma-delimited leaf.</summary>
	public const string LocList = "multiple locations are pipe-delimited (loc|loc|...) so they sit inside the comma-delimited line";

	/// <summary>How a name that itself contains a comma is encoded inside a comma-delimited leaf.</summary>
	public const string ListFieldQuoting =
		"a type or member name that itself contains a comma (a generic type with several type arguments, e.g. "
		+ "Dictionary<string, int>) is wrapped in single quotes so the comma is not read as a field separator";

	/// <summary>The common output preamble: a text block, not JSON.</summary>
	public const string TextNotJson =
		"Returns a compact text outline, not JSON: '#'-prefixed header lines, a blank line, then a "
		+ "tab-indented body. Newlines are '\\n'; booleans are Y or N. A #status header is present only when the solution is not "
		+ "Ready (Building or Faulted); its absence means Ready.";

	/// <summary>How a capped (paginated) result announces that it dropped rows, when the total is known.</summary>
	public const string Truncation =
		"If the result is capped at maxResults, a #count=<total available> and #truncated=Y header precede the "
		+ "body; both are absent when nothing was dropped, so the body is then the complete set.";

	/// <summary>
	/// How a capped result announces truncation when it cannot cheaply know the total (a scan that
	/// early-exits), so only the flag is emitted.
	/// </summary>
	public const string TruncationFlag =
		"A #truncated=Y header is present only when more results exist beyond maxResults; it is absent otherwise.";

	/// <summary>The shared failure shape every tool falls back to.</summary>
	public const string ErrorBlock =
		"On failure the result is header only: #error=<Indexing|NotFound|Ambiguous|...>, #errorMessage=..., "
		+ "and zero or more #candidate=<fqn>.";
}
