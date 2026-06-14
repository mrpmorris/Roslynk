using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Callers.GetCallersTests;

public class GetCallersTests
{
	[Fact]
	public async Task WhenAMethodIsCalled_ThenItsCallersAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCallersTool(registry, new SymbolResolver());

		GetCallersResult result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.Greeter.Greet");

		Assert.True(result.IsSuccess);
		Assert.NotEmpty(result.Callers!);
		Assert.Contains(result.Callers!, caller => caller.Contains("Caller.Run", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCallersTool(registry, new SymbolResolver());

		GetCallersResult result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCallersTool(registry, new SymbolResolver());

		GetCallersResult result = await subject.GetCallers(TestSolutions.Simple, "SimpleLibrary.Greeter.Greet");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
