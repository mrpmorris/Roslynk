using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes compiler diagnostics for a solution, optionally limited to one target framework of a
/// multi-targeted project. Analyzer diagnostics (via CompilationWithAnalyzers) and change-scoped
/// incremental runs are layered on in later tiers; this is the compiler pass.
/// </summary>
public sealed class DiagnosticsService
{
	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, string? targetFramework = null, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var results = new List<Diagnostic>();
		foreach (Project project in solution.Projects)
		{
			if (!ProjectFramework.Matches(project, targetFramework))
				continue;

			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is not null)
				results.AddRange(compilation.GetDiagnostics(cancellationToken));
		}

		return results;
	}
}
