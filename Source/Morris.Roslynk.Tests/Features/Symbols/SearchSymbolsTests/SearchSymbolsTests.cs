using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.SearchSymbolsTests;

public class SearchSymbolsTests
{
	[Fact]
	public async Task WhenSearchingByNameSubstring_ThenMatchingSymbolsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry);

		SearchSymbolsResult result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Results!, item => item.FullName == "SimpleLibrary.Greeter");
	}

	[Fact]
	public async Task WhenNothingMatchesTheQuery_ThenAnEmptyListIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry);

		SearchSymbolsResult result = await subject.SearchSymbols(TestSolutions.Simple, "NoSuchSymbolNameHere");

		Assert.True(result.IsSuccess);
		Assert.Empty(result.Results!);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new SearchSymbolsTool(registry);

		SearchSymbolsResult result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
