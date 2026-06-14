namespace Morris.Roslynk.Features.Symbols.GetSymbol;

/// <summary>
/// A symbol's headline details, enough to act on in one round-trip. Source location is null for symbols
/// with no in-source declaration (e.g. metadata); line/column are 1-based.
/// </summary>
public sealed record SymbolDto(
	string Name,
	string FullName,
	string Kind,
	string Accessibility,
	string Signature,
	string? SourcePath,
	int? StartLine,
	int? StartColumn,
	int? EndLine,
	int? EndColumn);
