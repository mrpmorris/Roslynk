using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Computes diagnostics for a solution; the compiler pass by default, or the compiler plus the project's
/// configured analyzers (NetAnalyzers etc.) when requested. Running analyzers is slower, so it is opt-in.
/// </summary>
public sealed class DiagnosticsService
{
	// Diagnostics are a pure function of the immutable Compilation and, with analyzers, the project's
	// AnalyzerOptions (additional files, .editorconfig), so results are memoized per compilation object. An
	// unchanged project keeps the same Compilation and AnalyzerOptions references across solution forks, so
	// every diagnostics pass skips it; an edit produces new objects and a recompute. Keyed weakly so entries
	// die with their compilation.
	private static readonly ConditionalWeakTable<Compilation, ResultBox> Memo = new();

	private sealed class ResultBox
	{
		public ImmutableArray<Diagnostic>? CompilerOnly;
		public (AnalyzerOptions Options, ImmutableArray<Diagnostic> Diagnostics)? WithAnalyzers;
	}

	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, bool includeAnalyzers = false, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("compute_diagnostics"))
		{
			activity?.SetTag("roslynk.analyzers", includeAnalyzers);

			Project[] projects = [.. solution.Projects];
			// Each project's analyzer run is internally parallel (concurrentAnalysis: true), so a handful of
			// projects at a time already saturates the machine; more slots than that only thrash.
			using var parallelism = new SemaphoreSlim(4);
			ImmutableArray<Diagnostic>[] perProject = await Task.WhenAll(projects.Select(project => Task.Run(async () =>
			{
				await parallelism.WaitAsync(cancellationToken);
				try
				{
					return await GetProjectDiagnosticsAsync(project, includeAnalyzers, cancellationToken);
				}
				finally
				{
					_ = parallelism.Release();
				}
			}, cancellationToken)));

			// Concatenated in solution order, so the result does not depend on which project finished first.
			var results = new List<Diagnostic>();
			foreach (ImmutableArray<Diagnostic> diagnostics in perProject)
				results.AddRange(diagnostics);

			activity?.SetTag("roslynk.diagnostic.count", results.Count);
			return results;
		}
	}

	private static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project, bool includeAnalyzers, CancellationToken cancellationToken)
	{
		Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return [];

		ResultBox box = Memo.GetValue(compilation, _ => new ResultBox());
		AnalyzerOptions options = project.AnalyzerOptions;
		lock (box)
		{
			if (!includeAnalyzers && box.CompilerOnly is ImmutableArray<Diagnostic> compilerOnly)
				return compilerOnly;
			if (includeAnalyzers && box.WithAnalyzers is (AnalyzerOptions memoOptions, var withAnalyzers) && ReferenceEquals(memoOptions, options))
				return withAnalyzers;
		}

		ImmutableArray<DiagnosticAnalyzer> analyzers =
			includeAnalyzers
				? AnalyzerDriverFactory.AnalyzersFor(project)
				: [];
		ImmutableArray<Diagnostic> diagnostics = analyzers.IsEmpty
			? compilation.GetDiagnostics(cancellationToken)
			: await AnalyzerDriverFactory.Create(project, compilation, analyzers).GetAllDiagnosticsAsync(cancellationToken);

		lock (box)
		{
			if (includeAnalyzers)
				box.WithAnalyzers = (options, diagnostics);
			else
				box.CompilerOnly = diagnostics;
		}

		return diagnostics;
	}
}
