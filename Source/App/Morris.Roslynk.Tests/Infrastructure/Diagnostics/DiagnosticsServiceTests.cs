using Microsoft.CodeAnalysis;
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
}
