namespace Morris.Roslynk.Features.References.FindReferences;

/// <summary>A single in-source reference location. Line/column are 1-based.</summary>
public sealed record ReferenceDto(
	string SourcePath,
	int StartLine,
	int StartColumn,
	int EndLine,
	int EndColumn);
