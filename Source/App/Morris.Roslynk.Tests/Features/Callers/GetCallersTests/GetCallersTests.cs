using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCallersTests;

public class GetCallersTests
{
	[Fact]
	public async Task WhenAMethodIsCalled_ThenItsCallersAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.Greeter.Greet");

		Assert.Contains("resolvedSymbol=SimpleLibrary.Greeter.Greet", result);
		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Caller\n", result);
		Assert.Contains("method,Run,", result);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.Greeter.Greet");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
