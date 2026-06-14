namespace Morris.Roslynk.Infrastructure.Symbols;

/// <summary>
/// The three interchangeable ways a caller can name a symbol — an exact doc-comment id, a fuzzy /
/// partial fully-qualified name, or a file position. Exactly one is expected to be supplied.
/// </summary>
public sealed record SymbolReference
{
	public string? SymbolId { get; init; }
	public string? Fqn { get; init; }
	public FilePosition? Position { get; init; }
}
