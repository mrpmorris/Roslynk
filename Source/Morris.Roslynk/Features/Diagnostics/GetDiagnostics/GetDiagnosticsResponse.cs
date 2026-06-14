namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>The diagnostics for an opened solution, errors first, with always-present per-severity counts.</summary>
public sealed record GetDiagnosticsResponse(
	IReadOnlyList<DiagnosticDto> Diagnostics,
	DiagnosticCounts Counts);
