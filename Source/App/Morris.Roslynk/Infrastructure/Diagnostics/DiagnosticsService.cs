using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes diagnostics for a solution; the compiler pass by default, or the compiler plus the project's
/// configured analyzers (NetAnalyzers etc.) when requested. Running analyzers is slower, so it is opt-in.
/// </summary>
public sealed class DiagnosticsService
{
	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, bool includeAnalyzers = false, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("compute_diagnostics"))
		{
			activity?.SetTag("roslynk.analyzers", includeAnalyzers);

			var results = new List<Diagnostic>();
			foreach (Project project in solution.Projects)
				results.AddRange(await GetProjectDiagnosticsAsync(project, includeAnalyzers, cancellationToken));

			activity?.SetTag("roslynk.diagnostic.count", results.Count);
			return results;
		}
	}

	/// <summary>
	/// Compiler diagnostics for <paramref name="project"/>, plus its configured analyzers when
	/// <paramref name="includeAnalyzers"/> is true — same rules as <see cref="GetAllDiagnosticsAsync"/> for one project.
	/// Used by the code-fix path so list and fix share one analyzer-aware source without re-scanning the whole solution.
	/// </summary>
	public async Task<IReadOnlyList<Diagnostic>> GetProjectDiagnosticsAsync(Project project, bool includeAnalyzers = false, CancellationToken cancellationToken = default)
	{
		if (project is null)
			throw new ArgumentNullException(nameof(project));

		Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return [];

		if (includeAnalyzers && TryGetAnalyzers(project, out ImmutableArray<DiagnosticAnalyzer> analyzers))
		{
			CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
			return await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken);
		}

		return compilation.GetDiagnostics(cancellationToken);
	}

	private static bool TryGetAnalyzers(Project project, out ImmutableArray<DiagnosticAnalyzer> analyzers)
	{
		analyzers = project.AnalyzerReferences
			.SelectMany(reference => reference.GetAnalyzers(project.Language))
			.ToImmutableArray();
		return !analyzers.IsEmpty;
	}
}
