using Morris.Roslynk.Features.Symbols.FindImplementations;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.FindImplementationsTests;

public class FindImplementationsTests
{
	[Fact]
	public async Task WhenAnInterfaceIsRequested_ThenItsImplementorsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindImplementationsTool(registry, new SymbolResolver());

		FindImplementationsResult result = await subject.FindImplementations(TestSolutions.Simple, "SimpleLibrary.IGreeter");

		Assert.True(result.IsSuccess);
		Assert.Contains("SimpleLibrary.Greeter", result.Implementations!);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindImplementationsTool(registry, new SymbolResolver());

		FindImplementationsResult result = await subject.FindImplementations(TestSolutions.Simple, "SimpleLibrary.IGreeter");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
