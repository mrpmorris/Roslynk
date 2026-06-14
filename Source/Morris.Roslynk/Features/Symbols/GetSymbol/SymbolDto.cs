using Morris.Roslynk.Infrastructure.Documentation;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

/// <summary>
/// A symbol's headline details, enough to act on in one round-trip. Source location is null for symbols
/// with no in-source declaration (e.g. metadata); line/column are 1-based. <c>SourceType</c> is
/// <c>source</c> for solution code or <c>metadata</c> for a referenced assembly (with <c>Assembly</c>
/// set). <c>Documentation</c> is the normalized, possibly inherited doc view — a derived read-only field,
/// never the source span to edit.
/// </summary>
public sealed record SymbolDto(
	string Name,
	string FullName,
	string Kind,
	string Accessibility,
	string Signature,
	string SourceType,
	string? Assembly,
	string? SourcePath,
	int? StartLine,
	int? StartColumn,
	int? EndLine,
	int? EndColumn,
	SymbolDocumentation Documentation);
