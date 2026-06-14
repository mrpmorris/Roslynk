using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes compiler diagnostics for a solution. Analyzer diagnostics (via CompilationWithAnalyzers)
/// and change-scoped incremental runs are layered on in later tiers; this is the compiler pass.
/// </summary>
public sealed class DiagnosticsService
{
	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var results = new List<Diagnostic>();
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is not null)
				results.AddRange(compilation.GetDiagnostics(cancellationToken));
		}

		return results;
	}
}
