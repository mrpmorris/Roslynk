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
		var subject = new FindImplementationsTool(registry, new SymbolResolver());

		FindImplementationsResponse response = await subject.FindImplementations(TestSolutions.Simple, "SimpleLibrary.IGreeter");

		Assert.Contains("SimpleLibrary.Greeter", response.Implementations);
	}
}
