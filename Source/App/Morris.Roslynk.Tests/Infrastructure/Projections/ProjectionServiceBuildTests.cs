using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Infrastructure.Projections;

public class ProjectionServiceBuildTests
{
	[Fact]
	public async Task WhenAConditionIsNestedInAnInactiveBranch_ThenItGetsAProjection()
	{
		Solution solution = SolutionWith(
			"""
			class C
			{
			#if OUTER
			#if INNER
				void M() { }
			#elif OTHER
			#endif
			#endif
			}
			""");

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(solution);

		Assert.Equal(["INNER", "OTHER", "OUTER", "base"], projections.Select(projection => projection.Label).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task WhenNoDocumentHasAConditionDirective_ThenOnlyTheBaseProjectionExists()
	{
		Solution solution = SolutionWith(
			"""
			#region Body
			class C { }
			#endregion
			""");

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(solution);

		Assert.Equal(["base"], projections.Select(projection => projection.Label));
	}

	[Fact]
	public async Task WhenOnlyOneProjectTestsASymbol_ThenItsVariantLeavesTheOtherProjectsUntouched()
	{
		// The other projects parse identically either way, so they keep their trees and compilations.
		Solution solution = SolutionWith(
			"""
			class C
			{
			#if FEATURE
				void M() { }
			#endif
			}
			""");
		Project untouched = solution.AddProject("Q", "Q", LanguageNames.CSharp).WithParseOptions(new CSharpParseOptions()).AddDocument("D.cs", "class D { }").Project;
		solution = untouched.Solution;

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(solution);

		Projection variant = Assert.Single(projections, projection => projection.Label == "FEATURE");
		Assert.Same(untouched.ParseOptions, variant.Solution.GetProject(untouched.Id)!.ParseOptions);
		Assert.Contains("FEATURE", variant.Solution.Projects.Single(project => project.Name == "P").ParseOptions!.PreprocessorSymbolNames);
	}

	private static Solution SolutionWith(string source)
	{
		var workspace = new AdhocWorkspace();
		Project project = workspace.CurrentSolution
			.AddProject("P", "P", LanguageNames.CSharp)
			.WithParseOptions(new CSharpParseOptions());
		return project.AddDocument("C.cs", source).Project.Solution;
	}
}
