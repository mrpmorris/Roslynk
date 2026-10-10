using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// Generic names resolve however they are written — bare, constructed, or with a backtick metadata arity —
/// with the exact pass winning over the relaxed one and every projection held to the strictest level.
/// Fixture: the MultiTarget solution's Generics.cs (Repro.Box&lt;T&gt;, Pair/Pair&lt;T&gt;, Multi&lt;T&gt;/Multi&lt;T1, T2&gt;).
/// </summary>
public class GenericNameResolutionTests
{
	[Theory]
	[InlineData("Repro.Box")]
	[InlineData("Repro.Box`1")]
	[InlineData("Repro.Box<X>")]
	[InlineData("Repro.Box<int>")]
	[InlineData("Repro.Box<>")]
	public async Task WhenATypeNameIsWrittenWithoutOrWithAnArity_ThenOneTypeResolves(string name)
	{
		INamedTypeSymbol type = await ResolveSingleNamedTypeAsync(name);

		Assert.Equal(1, type.Arity);
		Assert.Equal("Repro.Box<T>", type.ToDisplayString());
	}

	[Fact]
	public async Task WhenAMemberIsNamedThroughABareGenericContainer_ThenTheMemberResolves()
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync("Repro.Box.Get");

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(group[0].Symbol);
		Assert.Equal("Repro.Box<T>.Get()", SymbolSignature.Of(method));
	}

	[Fact]
	public async Task WhenSeveralGenericAritiesShareAName_ThenBothResolveForAnAmbiguity()
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync("Repro.Multi");

		Assert.Equal(2, groups.Count);
		Assert.Equal([1, 2], groups.Select(group => Assert.IsAssignableFrom<INamedTypeSymbol>(group[0].Symbol).Arity).Order());
	}

	[Fact]
	public async Task WhenANonGenericSharesItsNameWithAGeneric_ThenTheNonGenericWins()
	{
		INamedTypeSymbol type = await ResolveSingleNamedTypeAsync("Repro.Pair");

		Assert.Equal(0, type.Arity);
	}

	[Theory]
	[InlineData("Repro.Pair`1", 1)]
	[InlineData("Repro.Pair`0", 0)]
	public async Task WhenAnArityIsWrittenOnABareName_ThenOnlyThatArityResolves(string name, int expectedArity)
	{
		INamedTypeSymbol type = await ResolveSingleNamedTypeAsync(name);

		Assert.Equal(expectedArity, type.Arity);
	}

	[Theory]
	[InlineData("Repro.Box`2")]
	[InlineData("Repro.Box.GetMissing")]
	[InlineData("Repro.Pair`5")]
	public async Task WhenNothingMatchesTheWrittenShape_ThenNothingResolves(string name)
	{
		Assert.Empty(await ResolveGroupsAsync(name));
	}

	[Theory]
	[InlineData("Repro.Box<T>.this[int]")]
	[InlineData("Repro.Box.this[int]")]
	[InlineData("Repro.Box`1.this[int]")]
	public async Task WhenAnIndexerOfAGenericTypeIsNamed_ThenItResolves(string name)
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync(name);

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		IPropertySymbol indexer = Assert.IsAssignableFrom<IPropertySymbol>(group[0].Symbol);
		Assert.True(indexer.IsIndexer);
		Assert.Equal("Repro.Box<T>.this[int]", SymbolSignature.Of(indexer));
	}

	[Theory]
	[InlineData("Repro.Box.Box")]
	[InlineData("Repro.Box`1.Box()")]
	public async Task WhenAConstructorIsNamedThroughABareGenericName_ThenItResolves(string name)
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync(name);

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		IMethodSymbol constructor = Assert.IsAssignableFrom<IMethodSymbol>(group[0].Symbol);
		Assert.Equal(MethodKind.Constructor, constructor.MethodKind);
	}

	[Fact]
	public async Task WhenALocalFunctionIsNamedThroughABareGenericContainer_ThenItResolves()
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync("Repro.Converter.Parse.Tokenize");

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		IMethodSymbol local = Assert.IsAssignableFrom<IMethodSymbol>(group[0].Symbol);
		Assert.Equal("Tokenize", local.Name);
		Assert.Equal("Repro.Converter<T>.Parse().Tokenize()", SymbolSignature.Of(local));
	}

	[Fact]
	public async Task WhenAGenericTypeIsDeclaredInAnInactiveBranchWithItsNonGenericSibling_ThenTheNonGenericWins()
	{
		Solution solution = SolutionWith(
			"""
			namespace N
			{
				class Pair<T> { }
			#if LEGACY
				class Pair { }
			#endif
			}
			""");

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups =
			await new ProjectionService().ResolveAsync(new SymbolResolver(), projections, "N.Pair");

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(group[0].Symbol);
		Assert.Equal(0, type.Arity);
	}

	[Fact]
	public async Task WhenAGenericBclTypeIsNamedWithEitherSpelling_ThenTheMetadataTypeResolves()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var resolver = new SymbolResolver();

		foreach (string name in new[] { "System.Collections.Generic.List`1", "System.Collections.Generic.List<T>" })
		{
			IReadOnlyList<ISymbol> matches = await resolver.FindByFullyQualifiedNameWithMetadataAsync(instance.CurrentSolution, name);
			INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(matches));
			Assert.Equal(1, type.Arity);
		}
	}

	[Fact]
	public async Task WhenAGenericBclMemberIsNamedWithEitherSpelling_ThenTheMemberResolves()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var resolver = new SymbolResolver();

		foreach (string name in new[] { "System.Collections.Generic.List`1.Add", "System.Collections.Generic.List<T>.Add" })
		{
			IReadOnlyList<ISymbol> matches = await resolver.FindByFullyQualifiedNameWithMetadataAsync(instance.CurrentSolution, name);
			IMethodSymbol method = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(matches));
			Assert.Equal("Add", method.Name);
		}
	}

	private static async Task<INamedTypeSymbol> ResolveSingleNamedTypeAsync(string name)
	{
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ResolveGroupsAsync(name);

		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		return Assert.IsAssignableFrom<INamedTypeSymbol>(group[0].Symbol);
	}

	/// <summary>Resolves through the projection pipeline, so the fixture's per-TFM copies collapse to one group.</summary>
	private static async Task<IReadOnlyList<IReadOnlyList<ProjectionSymbol>>> ResolveGroupsAsync(string name)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var resolver = new SymbolResolver();

		IReadOnlyList<Projection> projections = await new ProjectionService().BuildAsync(instance.CurrentSolution);
		return await new ProjectionService().ResolveAsync(resolver, projections, name);
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
