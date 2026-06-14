namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>Per-severity totals across the whole solution, always returned so filtering is never silent.</summary>
public sealed class DiagnosticCounts
{
	public int Errors { get; }
	public int Warnings { get; }
	public int Infos { get; }
	public int Hidden { get; }

	public DiagnosticCounts(int errors, int warnings, int infos, int hidden)
	{
		Errors = errors;
		Warnings = warnings;
		Infos = infos;
		Hidden = hidden;
	}
}
