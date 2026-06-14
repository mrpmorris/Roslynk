using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class FindReferencesTests
{
	[Fact]
	public async Task WhenAReferencedTypeIsRequested_ThenItsReferencesAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResult result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.ResolvedSymbol);
		Assert.NotEmpty(result.References!);
	}

	[Fact]
	public async Task WhenTheSymbolIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResult result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenMoreReferencesMatchThanMaxResults_ThenTheResultIsTruncated()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResult result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter", maxResults: 0);

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.ResolvedSymbol);
		Assert.Empty(result.References!);
		Assert.True(result.Truncated);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResult result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
