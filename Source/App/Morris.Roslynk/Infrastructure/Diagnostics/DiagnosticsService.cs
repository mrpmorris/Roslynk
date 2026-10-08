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
/// <para>
/// Each project's diagnostics are memoized against what they are computed from: its syntax trees (generated
/// ones included), compilation options, metadata and analyzer references, the declarations of the projects it
/// references (their dependent semantic versions) and, with analyzers, its analyzer options (additional files,
/// .editorconfig). A project an edit did not touch is therefore never recomputed, and neither is one whose
/// dependencies changed only inside method bodies: Roslyn forks its compilation, but that cannot change its
/// diagnostics. An analyzer run also yields the compiler-only result, so the two never compile a project twice.
/// </para>
/// </summary>
public sealed class DiagnosticsService
{
	/// <summary>Each project's analyzer run is internally parallel (concurrentAnalysis: true), so a handful of
	/// projects at a time already saturates the machine; more only thrash.</summary>
	private const int ProjectParallelism = 4;

	// Keyed weakly by ProjectId, which every fork of one loaded solution shares, so entries die with the workspace.
	private static readonly ConditionalWeakTable<ProjectId, Entry> Memo = new();

	public async Task<IReadOnlyList<Diagnostic>> GetAllDiagnosticsAsync(Solution solution, bool includeAnalyzers = false, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("compute_diagnostics"))
		{
			activity?.SetTag("roslynk.analyzers", includeAnalyzers);

			Project[] projects = [.. solution.Projects];
			var perProject = new ImmutableArray<Diagnostic>[projects.Length];
			await Parallel.ForEachAsync(
				Enumerable.Range(0, projects.Length),
				new ParallelOptions { MaxDegreeOfParallelism = ProjectParallelism, CancellationToken = cancellationToken },
				async (index, token) => perProject[index] = await GetProjectDiagnosticsAsync(projects[index], includeAnalyzers, token));

			// Concatenated in solution order, so the result does not depend on which project finished first.
			var results = new List<Diagnostic>();
			foreach (ImmutableArray<Diagnostic> diagnostics in perProject)
				results.AddRange(diagnostics);

			activity?.SetTag("roslynk.diagnostic.count", results.Count);
			return results;
		}
	}

	/// <summary>The compiler's diagnostics for <paramref name="project"/>, from the same memo as <see cref="GetAllDiagnosticsAsync"/>.</summary>
	public static Task<ImmutableArray<Diagnostic>> GetCompilerDiagnosticsAsync(Project project, CancellationToken cancellationToken = default) =>
		GetProjectDiagnosticsAsync(project, includeAnalyzers: false, cancellationToken);

	private static async Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project, bool includeAnalyzers, CancellationToken cancellationToken)
	{
		Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
		if (compilation is null)
			return [];

		Inputs inputs = await Inputs.OfAsync(project, compilation, cancellationToken);
		Entry entry = Memo.GetValue(project.Id, _ => new Entry());
		lock (entry)
		{
			if (entry.Inputs?.Matches(inputs) == true)
			{
				if (!includeAnalyzers && entry.CompilerOnly is ImmutableArray<Diagnostic> compilerOnly)
					return compilerOnly;
				if (includeAnalyzers && entry.WithAnalyzers is (AnalyzerOptions options, var withAnalyzers) && ReferenceEquals(options, project.AnalyzerOptions))
					return withAnalyzers;
			}
		}

		ImmutableArray<DiagnosticAnalyzer> analyzers =
			includeAnalyzers
				? AnalyzerDriverFactory.AnalyzersFor(project)
				: [];
		ImmutableArray<Diagnostic> diagnostics = analyzers.IsEmpty
			? compilation.GetDiagnostics(cancellationToken)
			: await AnalyzerDriverFactory.Create(project, compilation, analyzers).GetAllDiagnosticsAsync(cancellationToken);

		lock (entry)
		{
			if (entry.Inputs?.Matches(inputs) != true)
				entry.Reset(inputs);

			if (includeAnalyzers)
			{
				entry.WithAnalyzers = (project.AnalyzerOptions, diagnostics);
				entry.CompilerOnly ??= [.. diagnostics.Where(IsCompilerDiagnostic)];
			}
			else
			{
				entry.CompilerOnly = diagnostics;
			}
		}

		return diagnostics;
	}

	private static bool IsCompilerDiagnostic(Diagnostic diagnostic) =>
		diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Compiler);

	/// <summary>One project's memoized results and the inputs they were computed from; guarded by its own lock.</summary>
	private sealed class Entry
	{
		public Inputs? Inputs;
		public ImmutableArray<Diagnostic>? CompilerOnly;
		public (AnalyzerOptions Options, ImmutableArray<Diagnostic> Diagnostics)? WithAnalyzers;

		public void Reset(Inputs inputs)
		{
			Inputs = inputs;
			CompilerOnly = null;
			WithAnalyzers = null;
		}
	}

	/// <summary>Everything a project's diagnostics depend on, compared by reference (and by version for the projects it references).</summary>
	private sealed record Inputs(
		ImmutableArray<SyntaxTree> Trees,
		CompilationOptions Options,
		IReadOnlyList<MetadataReference> MetadataReferences,
		IReadOnlyList<AnalyzerReference> AnalyzerReferences,
		ImmutableArray<(ProjectId Project, VersionStamp Declarations)> Dependencies)
	{
		public static async Task<Inputs> OfAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
		{
			var dependencies = ImmutableArray.CreateBuilder<(ProjectId, VersionStamp)>();
			foreach (ProjectReference reference in project.ProjectReferences)
			{
				if (project.Solution.GetProject(reference.ProjectId) is Project dependency)
					dependencies.Add((dependency.Id, await dependency.GetDependentSemanticVersionAsync(cancellationToken)));
			}

			return new Inputs(
				[.. compilation.SyntaxTrees],
				compilation.Options,
				project.MetadataReferences,
				project.AnalyzerReferences,
				dependencies.ToImmutable());
		}

		public bool Matches(Inputs other) =>
			ReferenceEquals(Options, other.Options)
			&& Trees.AsSpan().SequenceEqual(other.Trees.AsSpan())
			&& MetadataReferences.SequenceEqual(other.MetadataReferences, ReferenceEqualityComparer.Instance)
			&& AnalyzerReferences.SequenceEqual(other.AnalyzerReferences, ReferenceEqualityComparer.Instance)
			&& Dependencies.SequenceEqual(other.Dependencies);
	}
}
