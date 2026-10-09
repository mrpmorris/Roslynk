using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Diagnostics;

public class DiagnosticsServiceTests
{
	[Fact]
	public async Task WhenAnotherProjectIsAdded_ThenAnUnchangedProjectsAnalyzerDiagnosticsAreReused()
	{
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		var subject = new DiagnosticsService();

		IReadOnlyList<Diagnostic> before = await subject.GetAllDiagnosticsAsync(solution, includeAnalyzers: true);
		Solution forked = solution.AddProject("Unrelated", "Unrelated", LanguageNames.CSharp).Solution;
		IReadOnlyList<Diagnostic> after = await subject.GetAllDiagnosticsAsync(forked, includeAnalyzers: true);

		Assert.Contains(before, diagnostic => !diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
		Assert.All(before.Zip(after), pair => Assert.Same(pair.First, pair.Second));
	}

	[Fact]
	public async Task WhenAnAdditionalDocumentChanges_ThenAnalyzerDiagnosticsAreRecomputed()
	{
		// Analyzers read additional files, so a change to them invalidates the project's analyzer results even
		// where the compilation itself is unaffected.
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		Project project = solution.Projects.Single();
		var subject = new DiagnosticsService();

		IReadOnlyList<Diagnostic> before = await subject.GetAllDiagnosticsAsync(solution, includeAnalyzers: true);
		Project forked = project.AddAdditionalDocument("notes.txt", "notes").Project;
		Assert.Same(await project.GetCompilationAsync(), await forked.GetCompilationAsync());
		IReadOnlyList<Diagnostic> after = await subject.GetAllDiagnosticsAsync(forked.Solution, includeAnalyzers: true);

		Diagnostic analyzerDiagnostic = before.First(diagnostic => !diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
		Assert.DoesNotContain(after, diagnostic => ReferenceEquals(diagnostic, analyzerDiagnostic));
	}

	[Fact]
	public async Task WhenADependencyChangesOnlyInsideAMethodBody_ThenTheDependentsDiagnosticsAreReused()
	{
		// Roslyn forks App's compilation when Lib changes at all, but a body-only edit cannot change App's diagnostics.
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.RazorMultiProject)).CurrentSolution;
		Project app = solution.Projects.Single(project => project.Name == "App");
		ImmutableArray<Diagnostic> before = await DiagnosticsService.GetCompilerDiagnosticsAsync(app);

		Solution forked = await EditCodeBehindAsync(solution, "public void Refresh() => StateHasChanged();", "public void Refresh() => InvokeAsync(StateHasChanged);");
		Project forkedApp = forked.GetProject(app.Id)!;
		Assert.NotSame(await app.GetCompilationAsync(), await forkedApp.GetCompilationAsync());
		ImmutableArray<Diagnostic> after = await DiagnosticsService.GetCompilerDiagnosticsAsync(forkedApp);

		Assert.NotEmpty(before);
		Assert.True(before == after, "App's diagnostics were recomputed");
	}

	[Fact]
	public async Task WhenADependencysDeclarationsChange_ThenTheDependentsDiagnosticsAreRecomputed()
	{
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.RazorMultiProject)).CurrentSolution;
		Project app = solution.Projects.Single(project => project.Name == "App");
		ImmutableArray<Diagnostic> before = await DiagnosticsService.GetCompilerDiagnosticsAsync(app);

		Solution forked = await EditCodeBehindAsync(solution, "public void Refresh()", "public void Reload()");
		ImmutableArray<Diagnostic> after = await DiagnosticsService.GetCompilerDiagnosticsAsync(forked.GetProject(app.Id)!);

		Assert.False(before == after, "App's diagnostics were reused across a declaration change");
	}

	[Fact]
	public async Task WhenAnalyzerDiagnosticsWereComputed_ThenTheCompilerOnlyResultIsTakenFromThem()
	{
		using var registry = new InstanceRegistry();
		Solution solution = (await registry.GetOrAddAsync(TestSolutions.Simple)).CurrentSolution;
		// A new document gives the project inputs no earlier test has memoized.
		Solution forked = solution.AddDocument(DocumentId.CreateNewId(solution.ProjectIds[0]), "Fresh.cs", "using System.Text; class Fresh { }");
		Project project = forked.GetProject(solution.ProjectIds[0])!;

		IReadOnlyList<Diagnostic> withAnalyzers = await new DiagnosticsService().GetAllDiagnosticsAsync(forked, includeAnalyzers: true);
		ImmutableArray<Diagnostic> compilerOnly = await DiagnosticsService.GetCompilerDiagnosticsAsync(project);

		Assert.NotEmpty(compilerOnly);
		Assert.All(compilerOnly, diagnostic => Assert.Contains(withAnalyzers, candidate => ReferenceEquals(candidate, diagnostic)));
	}

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
