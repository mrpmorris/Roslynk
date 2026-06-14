namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>Per-severity totals across the whole solution, always returned so filtering is never silent.</summary>
public sealed record DiagnosticCounts(int Errors, int Warnings, int Infos, int Hidden);
