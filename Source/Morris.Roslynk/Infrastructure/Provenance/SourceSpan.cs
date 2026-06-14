namespace Morris.Roslynk.Infrastructure.Provenance;

/// <summary>
/// A located span of source: both character and line/column coordinates, plus provenance and the
/// document version it was read against. <see cref="EndChar"/> is exclusive.
/// </summary>
public sealed class SourceSpan
{
	public string? SourcePath { get; }
	public SourceType SourceType { get; }
	public int DocumentVersion { get; }
	public int StartChar { get; }
	public int EndChar { get; }
	public int StartLine { get; }
	public int StartColumn { get; }
	public int EndLine { get; }
	public int EndColumn { get; }

	public int Length => EndChar - StartChar;

	public SourceSpan(
		string? sourcePath,
		SourceType sourceType,
		int documentVersion,
		int startChar,
		int endChar,
		int startLine,
		int startColumn,
		int endLine,
		int endColumn)
	{
		SourcePath = sourcePath;
		SourceType = sourceType;
		DocumentVersion = documentVersion;
		StartChar = startChar;
		EndChar = endChar;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
	}
}
