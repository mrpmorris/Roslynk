using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Features.Symbols.SearchSymbolsTests;

public class SearchSymbolsTests
{
	[Fact]
	public async Task WhenSearchingByNameSubstring_ThenMatchingSymbolsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("class,Greeter", result);
	}

	[Fact]
	public async Task WhenNothingMatchesTheQuery_ThenNoResultsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "NoSuchSymbolNameHere");

		Assert.Equal("", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
