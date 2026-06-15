using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes diagnostics for a solution; the compiler pass by default, or the compiler plus the project's
/// configured analyzers (NetAnalyzers etc.) when requested; optionally limited to one target framework of
/// a multi-targeted project. Running analyzers is slower, so it is opt-in.
/// </summary>
public sealed class DiagnosticsService
{
	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, string? targetFramework = null, bool includeAnalyzers = false, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var results = new List<Diagnostic>();
		foreach (Project project in solution.Projects)
		{
			if (!ProjectFramework.Matches(project, targetFramework))
				continue;

			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			if (includeAnalyzers && TryGetAnalyzers(project, out ImmutableArray<DiagnosticAnalyzer> analyzers))
			{
				CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
				results.AddRange(await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken));
			}
			else
			{
				results.AddRange(compilation.GetDiagnostics(cancellationToken));
			}
		}

		return results;
	}

	private static bool TryGetAnalyzers(Project project, out ImmutableArray<DiagnosticAnalyzer> analyzers)
	{
		analyzers = project.AnalyzerReferences
			.SelectMany(reference => reference.GetAnalyzers(project.Language))
			.ToImmutableArray();
		return !analyzers.IsEmpty;
	}
}
