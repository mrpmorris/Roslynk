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

	private static Solution SolutionWith(string source)
	{
		var workspace = new AdhocWorkspace();
		Project project = workspace.CurrentSolution
			.AddProject("P", "P", LanguageNames.CSharp)
			.WithParseOptions(new CSharpParseOptions());
		return project.AddDocument("C.cs", source).Project.Solution;
	}
}
