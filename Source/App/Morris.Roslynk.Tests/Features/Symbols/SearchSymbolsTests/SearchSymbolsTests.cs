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

		Assert.DoesNotContain("error=", result);
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

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
	[Fact]
	public async Task WhenAQueryMatchesSeveralOverloads_ThenEveryOverloadIsListed()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Add");

		Assert.DoesNotContain("error=", result);
		// Both Ledger.Add overloads are listed; before the dedupe key carried the signature, one was dropped.
		Assert.Equal(2, result.Split('\n').Count(line => line.TrimStart('\t').StartsWith("method,Add,", StringComparison.Ordinal)));
	}
}
