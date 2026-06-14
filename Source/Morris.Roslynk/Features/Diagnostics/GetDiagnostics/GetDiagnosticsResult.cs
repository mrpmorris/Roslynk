using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

/// <summary>
/// The diagnostics for an opened solution, errors first, with always-present per-severity counts.
/// <see cref="Diagnostics"/> and <see cref="Counts"/> are null only when <see cref="ResultBase.Error"/>
/// carries an <see cref="ErrorCode.Indexing"/> because the solution is still loading.
/// </summary>
public sealed record GetDiagnosticsResult : ResultBase
{
	public IReadOnlyList<DiagnosticDto>? Diagnostics { get; }
	public DiagnosticCounts? Counts { get; }

	public GetDiagnosticsResult(SolutionModel solutionModel, Error? error, IReadOnlyList<DiagnosticDto>? diagnostics, DiagnosticCounts? counts)
		: base(solutionModel, error)
	{
		Diagnostics = diagnostics;
		Counts = counts;
	}
}
