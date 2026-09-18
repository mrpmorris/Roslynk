using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Projections;

public class ProjectionServiceKeyOfTests
{
	[Fact]
	public async Task WhenOverloadsAreKeyed_ThenTheKeysDiffer()
	{
		IReadOnlyList<ISymbol> overloads = await ResolveAsync("SimpleLibrary.Ledger.Add");

		Assert.Equal(2, overloads.Count);
		Assert.Equal(2, overloads.Select(ProjectionService.KeyOf).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task WhenAByRefOverloadIsKeyed_ThenItDiffersFromTheByValueOverload()
	{
		IReadOnlyList<ISymbol> byRef = await ResolveAsync("SimpleLibrary.Overloads.Pick(ref int)");
		IReadOnlyList<ISymbol> byValue = await ResolveAsync("SimpleLibrary.Overloads.Pick(int)");

		Assert.NotEqual(ProjectionService.KeyOf(byRef[0]), ProjectionService.KeyOf(byValue[0]));
	}

	[Fact]
	public async Task WhenTheSameMemberIsResolvedInSeveralProjections_ThenTheKeysMatch()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Conditional);

		var projectionService = new ProjectionService();
		IReadOnlyList<Projection> projections = await projectionService.BuildAsync(instance.CurrentSolution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups =
			await projectionService.ResolveAsync(new SymbolResolver(), projections, "ConditionalLib.Target.Ping");

		// Grouping is what proves the keys matched: one group, several per-projection instances inside it.
		IReadOnlyList<ProjectionSymbol> group = Assert.Single(groups);
		Assert.True(group.Count > 1);
		Assert.Single(group.Select(instance => ProjectionService.KeyOf(instance.Symbol)).Distinct(StringComparer.Ordinal));
	}

	[Fact]
	public async Task WhenAKeyIsSentBackAsAName_ThenItResolvesToThatOneSymbol()
	{
		IReadOnlyList<ISymbol> overloads = await ResolveAsync("SimpleLibrary.Ledger.Add");

		foreach (ISymbol overload in overloads)
		{
			IReadOnlyList<ISymbol> resolved = await ResolveAsync(ProjectionService.KeyOf(overload));

			ISymbol match = Assert.Single(resolved);
			Assert.Equal(ProjectionService.KeyOf(overload), ProjectionService.KeyOf(match));
		}
	}

	private static async Task<IReadOnlyList<ISymbol>> ResolveAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		return await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, symbolName);
	}
}
