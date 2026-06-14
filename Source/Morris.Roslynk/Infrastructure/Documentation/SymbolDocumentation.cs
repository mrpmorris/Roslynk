namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// A symbol's documentation as a shallow, normalized, read-only view: the standard sections with inline
/// tags reduced to markdown. <see cref="Source"/> is <c>own</c>, <c>inherited</c> (via
/// <c>&lt;inheritdoc/&gt;</c>, with <see cref="InheritedFrom"/> set), or <c>none</c>.
/// <see cref="IsLiteralSourceText"/> is always false — this is derived text, never the source span to edit.
/// </summary>
public sealed record SymbolDocumentation(
	string? Summary,
	IReadOnlyList<DocumentationParam> Params,
	string? Returns,
	string? Remarks,
	IReadOnlyList<DocumentationException> Exceptions,
	string Source,
	DocumentationInheritedFrom? InheritedFrom,
	bool IsLiteralSourceText)
{
	/// <summary>The value for a symbol that carries no documentation comment.</summary>
	public static SymbolDocumentation None { get; } =
		new(Summary: null, Params: [], Returns: null, Remarks: null, Exceptions: [], Source: "none", InheritedFrom: null, IsLiteralSourceText: false);
}
