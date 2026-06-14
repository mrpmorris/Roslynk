namespace Morris.Roslynk.Features.References.FindReferences;

/// <summary>A single in-source reference location. Line/column are 1-based.</summary>
public sealed class ReferenceDto
{
	public string SourcePath { get; }
	public int StartLine { get; }
	public int StartColumn { get; }
	public int EndLine { get; }
	public int EndColumn { get; }

	public ReferenceDto(
		string sourcePath,
		int startLine,
		int startColumn,
		int endLine,
		int endColumn)
	{
		SourcePath = sourcePath;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
	}
}
