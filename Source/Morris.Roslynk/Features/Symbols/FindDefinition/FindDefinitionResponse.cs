namespace Morris.Roslynk.Features.Symbols.FindDefinition;

/// <summary>
/// Where the symbol used at the requested position is declared. All fields are null when nothing
/// resolves there; the location fields are null for symbols with no in-source declaration (metadata).
/// Line/column are 1-based.
/// </summary>
public sealed record FindDefinitionResponse(
	string? FullName,
	string? Kind,
	string? SourcePath,
	int? StartLine,
	int? StartColumn,
	int? EndLine,
	int? EndColumn);
