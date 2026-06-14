using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Symbols.SearchSymbolsTests;

public class SearchSymbolsTests
{
	[Fact]
	public async Task WhenSearchingByNameSubstring_ThenMatchingSymbolsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new SearchSymbolsTool(registry);

		SearchSymbolsResponse response = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.Contains(response.Results, result => result.FullName == "SimpleLibrary.Greeter");
	}
}
