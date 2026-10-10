using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Projections;

/// <summary>
/// Two projects sharing one source file, like the per-TFM projects of a multi-targeted solution: P1 is
/// discovered first and cannot bind the declaration's parameter type, P2 can. <c>HAS_SYSTEM</c> is defined in
/// P2 only, so it is mixed across the projects and derives no toggle projection — both copies are found in
/// project order inside the base projection, giving fully controlled, load-order-sensitive ground for the
/// best-bound representative.
/// </summary>
public class ProjectionServiceResolveTests
{
	[Fact]
	public async Task WhenTheFirstCopyHasAnUnboundParameterType_ThenTheBoundCopyLeadsTheGroup()
	{
		Solution solution = SolutionWith(
			"""
			#if HAS_SYSTEM
			using System;
			#endif

			namespace Repro;

			public class Api
			{
				public void Take(Int32 value) { }
			}
			""");

		// The fixture must discover the unbound copy first, or this test proves nothing.
		ISymbol first = (await new SymbolResolver().FindByFullyQualifiedNameAsync(solution, "Repro.Api.Take"))[0];
		Assert.True(ErrorTypeCount.Of(first) > 0, "The unbound copy must be discovered first, or this test proves nothing.");

		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Take");

		ProjectionSymbol head = Assert.Single(groups)[0];
		Assert.Equal(SpecialType.System_Int32, ParameterTypeOf(head).SpecialType);
		Assert.Equal("Repro.Api.Take(int)", SymbolResolver.SignatureName(head.Symbol));
	}

	[Fact]
	public async Task WhenEveryCopyBindsEquallyWell_ThenDiscoveryOrderIsKept()
	{
		Solution solution = SolutionWith(
			"""
			#if HAS_SYSTEM
			using System;
			#endif

			namespace Repro;

			public class Api
			{
				public int Count() => 0;
			}
			""");

		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Count");

		// Both copies bind, so the stable sort keeps discovery order: the first project's copy still leads.
		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		Assert.Equal(2, group.Count);
		Assert.Equal("P1", ContainingAssemblyOf(group[0]));
	}

	[Fact]
	public async Task WhenTheNameSpellsTheBoundType_ThenTheMatchedCopiesFormOneGroup()
	{
		Solution solution = SolutionWith(TakeInt32Source);

		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Take(int)");

		// The bound spelling matches the bound copy; the group it forms is led by that copy. The group's size
		// depends on D2 (group completion): one copy today, the error copy joining once parameterized names
		// reach every copy.
		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		Assert.Equal(SpecialType.System_Int32, ParameterTypeOf(group[0]).SpecialType);
	}

	[Fact]
	public async Task WhenTheNameSpellsTheUnboundType_ThenTheMatchedCopiesFormOneGroup()
	{
		Solution solution = SolutionWith(TakeInt32Source);

		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Take(Int32)");

		// The bound copy's accepted texts are 'int'/'System.Int32', not 'Int32', so only the error copy
		// matches and the group is led by it. With D2 (group completion) it would be led by the bound copy.
		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		Assert.NotEqual(SpecialType.System_Int32, ParameterTypeOf(group[0]).SpecialType);
	}

	[Fact]
	public async Task WhenTheDeclarationIsInactiveInALinkedProject_ThenNoCopyIsAddedFromIt()
	{
		Solution solution = SolutionWith(
			"""
			#if HAS_SYSTEM
			using System;
			#endif

			namespace Repro;

			public class Api
			{
			#if FEATURE
				public void Take(Int32 value) { }
			#endif
			}
			""");

		// FEATURE is defined in P2 only, so P1's parse has no declaration node for it: the bare name yields
		// one group whose single copy is P2's (holds with or without D2).
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Take");

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		Assert.Single(group);
	}

	[Fact]
	public async Task WhenOverloadsShareAFile_ThenTheyStaySeparateGroups()
	{
		Solution solution = SolutionWith(
			"""
			#if HAS_SYSTEM
			using System;
			#endif

			namespace Repro;

			public class Api
			{
				public void Take(int value) { }

				public void Take(string text) { }
			}
			""");

		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveAsync(solution, "Repro.Api.Take");

		// Two overloads, each with one copy per project; neither absorbs the other's copies.
		Assert.Equal(2, groups.Count);
		Assert.All(groups, group => Assert.Equal(2, group.Count));
		Assert.Equal(
			["Repro.Api.Take(int)", "Repro.Api.Take(string)"],
			groups.Select(group => SymbolResolver.SignatureName(group[0].Symbol)).Order(StringComparer.Ordinal));
	}

	private const string TakeInt32Source =
		"""
		#if HAS_SYSTEM
		using System;
		#endif

		namespace Repro;

		public class Api
		{
			public void Take(Int32 value) { }
		}
		""";

	private static async Task<IReadOnlyList<IReadOnlyList<ProjectionSymbol>>> ResolveAsync(Solution solution, string name)
	{
		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(solution);
		return await new ProjectionService().ResolveAsync(new SymbolResolver(), projections, name);
	}

	private static ITypeSymbol ParameterTypeOf(ProjectionSymbol projectionSymbol) =>
		((IMethodSymbol)projectionSymbol.Symbol).Parameters[0].Type;

	private static string ContainingAssemblyOf(ProjectionSymbol projectionSymbol) =>
		projectionSymbol.Symbol.ContainingAssembly!.Name!;

	private static Solution SolutionWith(string source)
	{
		MetadataReference coreLibrary = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
		var workspace = new AdhocWorkspace();
		Solution solution = workspace.CurrentSolution
			.AddProject("P1", "P1", LanguageNames.CSharp)
			.WithParseOptions(new CSharpParseOptions())
			.WithMetadataReferences([coreLibrary])
			.Solution
			.AddProject("P2", "P2", LanguageNames.CSharp)
			.WithParseOptions(new CSharpParseOptions().WithPreprocessorSymbols(new[] { "HAS_SYSTEM", "FEATURE" }))
			.WithMetadataReferences([coreLibrary])
			.Solution;

		// One physical file, loaded into both projects like the per-TFM copies of a multi-targeted project.
		solution = solution.GetProject(solution.ProjectIds[0])!.AddDocument("Shared.cs", source).Project.Solution;
		return solution.GetProject(solution.ProjectIds[1])!.AddDocument("Shared.cs", source).Project.Solution;
	}
}
