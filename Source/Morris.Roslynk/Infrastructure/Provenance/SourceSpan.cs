namespace Morris.Roslynk.Infrastructure.Provenance;

/// <summary>
/// A located span of source: both character and line/column coordinates, plus provenance and the
/// document version it was read against. <see cref="EndChar"/> is exclusive.
/// </summary>
public sealed record SourceSpan(
	string? SourcePath,
	SourceType SourceType,
	int DocumentVersion,
	int StartChar,
	int EndChar,
	int StartLine,
	int StartColumn,
	int EndLine,
	int EndColumn)
{
	public int Length => EndChar - StartChar;
}
