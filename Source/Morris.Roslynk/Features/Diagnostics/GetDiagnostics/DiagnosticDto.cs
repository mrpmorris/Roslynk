namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>A single diagnostic. Line/column are 1-based; <c>SourcePath</c> is null when the diagnostic
/// has no in-source location.</summary>
public sealed record DiagnosticDto(
	string Id,
	string Severity,
	string Message,
	string? SourcePath,
	int StartLine,
	int StartColumn,
	int EndLine,
	int EndColumn);
