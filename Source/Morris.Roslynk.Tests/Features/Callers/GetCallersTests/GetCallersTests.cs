using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCallersTests;

public class GetCallersTests
{
	[Fact]
	public async Task WhenAMethodIsCalled_ThenItsCallersAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCallersTool(registry, new SymbolResolver());

		GetCallersResponse response = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.Greeter.Greet");

		Assert.NotEmpty(response.Callers);
		Assert.Contains(response.Callers, caller => caller.Contains("Caller.Run", StringComparison.Ordinal));
	}
}
