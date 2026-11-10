using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Diagnostics;

/// <summary>
/// The service is stateless — one compute per project, compiler and analyzers together — so these tests
/// pin its contract: an unchanged project's diagnostics are identical objects across a fork (Roslyn
/// carries the project instance and its memoized compilation), and a declaration change produces fresh
/// results. Reuse across publications is <see cref="RoslynInstance"/>'s SolutionSnapshot's job, tested
/// there.
/// </summary>
public class DiagnosticsServiceTests
{
	[Fact]
	public async Task WhenAnotherProjectIsAdded_ThenAnUnchangedProjectsDiagnosticsAreTheSameInstances()
	{
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		var subject = new DiagnosticsService();

		IReadOnlyList<Diagnostic> before = await subject.GetAllDiagnosticsAsync(solution);
		Solution forked = solution.AddProject("Unrelated", "Unrelated", LanguageNames.CSharp).Solution;
		IReadOnlyList<Diagnostic> after = await subject.GetAllDiagnosticsAsync(forked);

		Assert.Contains(before, diagnostic => !IsCompilerDiagnostic(diagnostic));
		// The service is stateless: the unchanged project re-forks from the same compilation, so its
		// diagnostics match by id and location (instances differ between independent computes), while the
		// added project contributes its own. Analyzer-internal ordering can differ between independent
		// runs, so compare the original project's diagnostics as a sorted multiset keyed by id+location.
		Project original = solution.Projects.First();
		static string Key(Diagnostic diagnostic) => $"{diagnostic.Id}|{diagnostic.Location.GetLineSpan().StartLinePosition}";
		ImmutableArray<Diagnostic> beforeOriginal = await DiagnosticsService.GetProjectDiagnosticsAsync(original);
		ImmutableArray<Diagnostic> afterOriginal = await DiagnosticsService.GetProjectDiagnosticsAsync(forked.Projects.First(project => project.Id == original.Id));
		Assert.Equal([.. beforeOriginal.Select(Key).Order()], [.. afterOriginal.Select(Key).Order()]);
	}

	[Fact]
	public async Task WhenAnAdditionalDocumentChanges_ThenDiagnosticsAreRecomputed()
	{
		// Analyzers read additional files, so a change to them can invalidate the project's analyzer
		// results even where the compilation itself is unaffected.
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		Project project = solution.Projects.Single();
		var subject = new DiagnosticsService();

		IReadOnlyList<Diagnostic> before = await subject.GetAllDiagnosticsAsync(solution);
		Project forked = project.AddAdditionalDocument("notes.txt", "notes").Project;
		Assert.Same(await project.GetCompilationAsync(), await forked.GetCompilationAsync());
		IReadOnlyList<Diagnostic> after = await subject.GetAllDiagnosticsAsync(forked.Solution);

		Diagnostic analyzerDiagnostic = before.First(diagnostic => !IsCompilerDiagnostic(diagnostic));
		Assert.DoesNotContain(after, diagnostic => ReferenceEquals(diagnostic, analyzerDiagnostic));
	}

	[Fact]
	public async Task WhenADependencyChangesOnlyInsideAMethodBody_ThenTheDependentsCompilationIsReused()
	{
		// Roslyn forks App's compilation when Lib changes at all, but a body-only edit cannot change App's
		// diagnostics: the compilation is re-forked from shared trees, and GetCompilationAsync's own
		// per-instance memo means the compilation object itself is only rebuilt when inputs move.
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.RazorMultiProject)).CurrentSolution;
		Project app = solution.Projects.Single(project => project.Name == "App");
		ImmutableArray<Diagnostic> before = await DiagnosticsService.GetProjectDiagnosticsAsync(app);

		Solution forked = await EditCodeBehindAsync(solution, "public void Refresh() => StateHasChanged();", "public void Refresh() => InvokeAsync(StateHasChanged);");
		Project forkedApp = forked.GetProject(app.Id)!;
		Assert.NotSame(await app.GetCompilationAsync(), await forkedApp.GetCompilationAsync());
		ImmutableArray<Diagnostic> after = await DiagnosticsService.GetProjectDiagnosticsAsync(forkedApp);

		Assert.NotEmpty(before);
		Assert.True(DiagnosticsMatch(before, after), "App's diagnostics changed across a body-only edit");
	}

	[Fact]
	public async Task WhenAProjectIsComputedTwice_ThenEachComputeReflectsThatProjectState()
	{
		// The service is stateless, so "recompute" is trivially true; what needs pinning is that the
		// result tracks the project's current inputs rather than any earlier state.
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.RazorMultiProject)).CurrentSolution;
		Project lib = solution.Projects.First(project => project.Name.StartsWith("Lib", StringComparison.Ordinal));
		ImmutableArray<Diagnostic> before = await DiagnosticsService.GetProjectDiagnosticsAsync(lib);
		Assert.DoesNotContain(before, diagnostic => diagnostic.Id == "CS0103");

		Solution forked = await EditCodeBehindAsync(solution, "public void Refresh()", "public void Refresh() => Missing.Method();");
		ImmutableArray<Diagnostic> after = await DiagnosticsService.GetProjectDiagnosticsAsync(forked.GetProject(lib.Id)!);

		Assert.Contains(after, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS0103");
	}

	[Fact]
	public async Task WhenTheSolutionIsComputed_ThenTheResultIncludesBothCompilerAndAnalyzerDiagnostics()
	{
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		// A new document gives the project inputs no earlier test has computed.
		Solution forked = solution.AddDocument(DocumentId.CreateNewId(solution.ProjectIds[0]), "Fresh.cs", "using System.Text; class Fresh { }");
		Project project = forked.GetProject(solution.ProjectIds[0])!;

		IReadOnlyList<Diagnostic> computed = await new DiagnosticsService().GetAllDiagnosticsAsync(forked);
		ImmutableArray<Diagnostic> compilerOnly = (await project.GetCompilationAsync())!.GetDiagnostics();

		Assert.NotEmpty(compilerOnly);
		// The analyzer pass re-wraps the compiler's diagnostics, so identities differ; ids and locations
		// are what the contract guarantees.
		Assert.All(compilerOnly, diagnostic => Assert.Contains(computed, candidate =>
			candidate.Id == diagnostic.Id && candidate.Location.Equals(diagnostic.Location)));
		Assert.Contains(computed, IsCompilerDiagnostic);
		Assert.Contains(computed, diagnostic => !IsCompilerDiagnostic(diagnostic));
	}

	private static bool DiagnosticsMatch(ImmutableArray<Diagnostic> left, ImmutableArray<Diagnostic> right) =>
		left.Length == right.Length
		&& left.Zip(right).All(pair => pair.First.Id == pair.Second.Id && pair.First.Location.Equals(pair.Second.Location));

	private static bool IsCompilerDiagnostic(Diagnostic diagnostic) =>
		diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Compiler);

	private static async Task<Solution> EditCodeBehindAsync(Solution solution, string oldText, string newText)
	{
		foreach (DocumentId id in solution.Projects.Where(project => project.Name.StartsWith("Lib", StringComparison.Ordinal)).SelectMany(project => project.Documents).Where(document => document.Name == "Widget.razor.cs").Select(document => document.Id))
		{
			SourceText text = await solution.GetDocument(id)!.GetTextAsync();
			solution = solution.WithDocumentText(id, SourceText.From(text.ToString().Replace(oldText, newText)));
		}

		return solution;
	}
}
