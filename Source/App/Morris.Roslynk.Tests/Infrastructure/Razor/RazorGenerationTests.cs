using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

/// <summary>
/// The in-process Razor generation: which projects get generated documents, in what order, from which
/// generator, and what the Razor compiler itself reports.
/// </summary>
public class RazorGenerationTests
{
	[Fact]
	public async Task WhenAProjectIsMultiTargeted_ThenEveryTargetFrameworkGetsItsOwnGeneratedDocuments()
	{
		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(TestSolutions.RazorMultiProject);

		Project[] targets = [.. workspace.Solution.Projects.Where(project => project.Name.StartsWith("Lib", StringComparison.Ordinal))];
		Assert.Equal(2, targets.Length);

		foreach (Project target in targets)
		{
			Assert.Contains(target.Documents, document =>
				RazorMapping.IsRazorGeneratedDocument(document) && document.FilePath!.EndsWith("Widget_razor.g.cs", StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain(target.AnalyzerReferences, RazorGeneratorLoader.IsRazorCompiler);
			Assert.True(workspace.Razor.Covers(target.Id));

			// The code-behind calls StateHasChanged, which only binds through the generated ComponentBase partial.
			Compilation compilation = (await target.GetCompilationAsync())!;
			Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
		}
	}

	[Fact]
	public async Task WhenADependentProjectIsListedFirst_ThenItsGeneratedCodeStillBindsTheReferencedComponent()
	{
		// Emulates a host whose analyzer loader refuses the SDK generator (the reason in-process generation
		// exists): with no native generator, App's Widget is a component only if Lib was generated first.
		MsBuildRegistrar.EnsureRegistered();
		using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
		Solution solution = await workspace.OpenSolutionAsync(TestSolutions.RazorMultiProject);
		Assert.Equal("App", solution.GetProject(solution.ProjectIds[0])!.Name);
		foreach (ProjectId projectId in solution.ProjectIds)
		{
			Project project = solution.GetProject(projectId)!;
			solution = solution.WithProjectAnalyzerReferences(projectId, project.AnalyzerReferences.Where(reference => !RazorGeneratorLoader.IsRazorCompiler(reference)));
		}

		solution = await RazorDocumentGenerator.AugmentAsync(solution, new RazorGenerationState());

		Project app = solution.Projects.Single(project => project.Name == "App");
		Document page = app.Documents.Single(document => document.FilePath!.EndsWith("Page_razor.g.cs", StringComparison.OrdinalIgnoreCase));
		string generated = (await page.GetTextAsync()).ToString();
		Assert.Contains("OpenComponent<global::Lib.Widget>", generated);
		Assert.DoesNotContain("OpenElement(0, \"Widget\")", generated);
	}

	[Fact]
	public async Task WhenTheProjectReferencesALoadableRazorCompiler_ThenItsOwnGeneratorInstanceIsUsed()
	{
		MsBuildRegistrar.EnsureRegistered();
		using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
		Solution solution = await workspace.OpenSolutionAsync(TestSolutions.Razor);
		Project project = solution.Projects.Single();
		AnalyzerReference? reference = project.AnalyzerReferences.FirstOrDefault(RazorGeneratorLoader.IsRazorCompiler);
		ISourceGenerator? own = reference?.GetGenerators(project.Language).FirstOrDefault();

		ISourceGenerator? generator = RazorGeneratorLoader.For(project);

		Assert.NotNull(generator);
		if (own is not null)
			Assert.Same(own, generator); // no second copy of the compiler is loaded
	}

	[Fact]
	public async Task WhenARazorFileHasASyntaxError_ThenTheRazorCompilersDiagnosticIsRecordedAgainstIt()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string broken = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Broken.razor");
		await File.WriteAllTextAsync(broken, "<h1>Broken</h1>\n@code {\n    private string Value = \"\";\n");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		Diagnostic diagnostic = Assert.Single(workspace.Razor.Diagnostics(workspace.Solution), d => d.Id == "RZ1006");
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Equal(broken, diagnostic.Location.GetLineSpan().Path, ignoreCase: true);
	}

	[Theory]
	[InlineData("C:/src/App/obj/RoslynkRazorGenerated/Views/Home/Index_cshtml.g.cs", true)]
	[InlineData("C:\\src\\App\\obj\\Debug\\net8.0\\generated\\Microsoft.CodeAnalysis.Razor.Compiler\\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator\\Counter_razor.g.cs", true)]
	[InlineData("C:/src/App/Gen/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Counter_razor.g.cs", true)]
	[InlineData("C:/src/App/Counter_razor.g.cs", false)]
	[InlineData("C:/src/App/obj/RoslynkRazorGenerated/Helper.g.cs", false)]
	public void WhenAPathIsClassified_ThenOnlyRazorOutputUnderAGeneratorFolderCounts(string path, bool expected) =>
		Assert.Equal(expected, RazorMapping.IsRazorGeneratedPath(path));
}
