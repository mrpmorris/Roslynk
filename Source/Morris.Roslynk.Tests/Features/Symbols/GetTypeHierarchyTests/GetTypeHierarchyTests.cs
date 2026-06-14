using Morris.Roslynk.Features.Symbols.GetTypeHierarchy;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.GetTypeHierarchyTests;

public class GetTypeHierarchyTests
{
	[Fact]
	public async Task WhenATypeImplementsAnInterface_ThenTheInterfaceIsInTheHierarchy()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver());

		GetTypeHierarchyResult result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.True(result.IsSuccess);
		Assert.Equal("SimpleLibrary.Greeter", result.ResolvedType);
		Assert.Contains("SimpleLibrary.IGreeter", result.Interfaces!);
	}

	[Fact]
	public async Task WhenTheTypeIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver());

		GetTypeHierarchyResult result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver());

		GetTypeHierarchyResult result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
