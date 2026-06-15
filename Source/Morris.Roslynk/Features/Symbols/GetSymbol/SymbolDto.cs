using Morris.Roslynk.Infrastructure.Documentation;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

/// <summary>
/// A symbol's headline details, enough to act on in one round-trip. Source location is null for symbols
/// with no in-source declaration (e.g. metadata); line/column are 1-based. <c>SourceType</c> is
/// <c>source</c> for solution code or <c>metadata</c> for a referenced assembly (with <c>Assembly</c>
/// set). <c>Documentation</c> is the normalized, possibly inherited doc view; a derived read-only field,
/// never the source span to edit.
/// </summary>
public sealed class SymbolDto
{
	public string Name { get; }
	public string FullName { get; }
	public string Kind { get; }
	public string Accessibility { get; }
	public string Signature { get; }
	public string SourceType { get; }
	public string? Assembly { get; }
	public string? SourcePath { get; }
	public int? StartLine { get; }
	public int? StartColumn { get; }
	public int? EndLine { get; }
	public int? EndColumn { get; }
	public SymbolDocumentation Documentation { get; }

	public SymbolDto(
		string name,
		string fullName,
		string kind,
		string accessibility,
		string signature,
		string sourceType,
		string? assembly,
		string? sourcePath,
		int? startLine,
		int? startColumn,
		int? endLine,
		int? endColumn,
		SymbolDocumentation documentation)
	{
		Name = name;
		FullName = fullName;
		Kind = kind;
		Accessibility = accessibility;
		Signature = signature;
		SourceType = sourceType;
		Assembly = assembly;
		SourcePath = sourcePath;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
		Documentation = documentation;
	}
}
