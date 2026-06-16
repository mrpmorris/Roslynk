using Morris.Roslynk.Features.Symbols.FindImplementations;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.FindImplementationsTests;

public class FindImplementationsTests
{
	[Fact]
	public async Task WhenAnInterfaceIsRequested_ThenItsImplementorsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindImplementationsTool(registry, new SymbolResolver());

		string result = await subject.FindImplementations(TestSolutions.Simple, "SimpleLibrary.IGreeter");

		Assert.Contains("#resolvedSymbol=SimpleLibrary.IGreeter", result);
		Assert.DoesNotContain("#error=", result);
		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("class,Greeter,", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindImplementationsTool(registry, new SymbolResolver());

		string result = await subject.FindImplementations(TestSolutions.Simple, "SimpleLibrary.IGreeter");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
