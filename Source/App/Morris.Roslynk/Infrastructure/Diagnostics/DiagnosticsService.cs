using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes diagnostics statelessly: one pass per project with all analyzers, no memoization of any
/// kind. Caching lives in <see cref="Morris.Roslynk.Infrastructure.Lifecycle.RoslynInstance"/>, whose
/// <c>SolutionSnapshot</c> keys per-project computation tasks by project instance and drops them with
/// the publication; this service only knows how to produce a result. Consumers decide which
/// diagnostics to include in their response: a diagnostic came from the compiler when its
/// <c>Descriptor.CustomTags</c> contains <see cref="WellKnownDiagnosticTags.Compiler"/>, and from an
/// analyzer otherwise.
/// </summary>
public sealed class DiagnosticsService
{
	/// <summary>Each project's analyzer run is internally parallel (concurrentAnalysis: true), so a handful of
	/// projects at a time already saturates the machine; more only thrash.</summary>
	private const int ProjectParallelism = 4;

	/// <summary>All diagnostics for <paramref name="solution"/>, concatenated in solution order so the
	/// result does not depend on which project finished first.</summary>
	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(solution);

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("compute_diagnostics"))
		{
			Project[] projects = [.. solution.Projects];
			var perProject = new ImmutableArray<Diagnostic>[projects.Length];
			await Parallel.ForEachAsync(
				Enumerable.Range(0, projects.Length),
				new ParallelOptions { MaxDegreeOfParallelism = ProjectParallelism, CancellationToken = cancellationToken },
				async (index, token) => perProject[index] = await GetProjectDiagnosticsAsync(projects[index], token));

			var collected = ImmutableArray.CreateBuilder<Diagnostic>();
			foreach (ImmutableArray<Diagnostic> diagnostics in perProject)
				collected.AddRange(diagnostics);

			ImmutableArray<Diagnostic> computed = collected.ToImmutable();
			activity?.SetTag("roslynk.diagnostic.count", computed.Length);
			return computed;
		}
	}

	/// <summary>One project's diagnostics: compiler and analyzer in a single pass. A project with no
	/// analyzers (or none applicable) returns the compiler's diagnostics alone.</summary>
	public static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken = default)
	{
		Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return [];

		ImmutableArray<DiagnosticAnalyzer> analyzers = AnalyzerDriverFactory.AnalyzersFor(project);
		return analyzers.IsEmpty
			? compilation.GetDiagnostics(cancellationToken)
			: await AnalyzerDriverFactory.Create(project, compilation, analyzers).GetAllDiagnosticsAsync(cancellationToken);
	}
}
