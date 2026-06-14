namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>A single diagnostic. Line/column are 1-based; <c>SourcePath</c> is null when the diagnostic
/// has no in-source location.</summary>
public sealed class DiagnosticDto
{
	public string Id { get; }
	public string Severity { get; }
	public string Message { get; }
	public string? SourcePath { get; }
	public int StartLine { get; }
	public int StartColumn { get; }
	public int EndLine { get; }
	public int EndColumn { get; }

	public DiagnosticDto(
		string id,
		string severity,
		string message,
		string? sourcePath,
		int startLine,
		int startColumn,
		int endLine,
		int endColumn)
	{
		Id = id;
		Severity = severity;
		Message = message;
		SourcePath = sourcePath;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
	}
}
