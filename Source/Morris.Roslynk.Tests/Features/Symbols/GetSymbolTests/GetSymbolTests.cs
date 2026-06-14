using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolTests;

public class GetSymbolTests
{
	[Fact]
	public async Task WhenAnExistingTypeIsRequested_ThenItsDetailsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.Symbol);
		Assert.Equal("Greeter", result.Symbol!.Name);
		Assert.Equal("NamedType", result.Symbol.Kind);
		Assert.EndsWith("Greeter.cs", result.Symbol.SourcePath);
	}

	[Fact]
	public async Task WhenTheSymbolDoesNotExist_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenTheSymbolHasDocumentation_ThenItIsIncluded()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.Symbol);
		Assert.Equal("own", result.Symbol!.Documentation.Source);
		Assert.NotNull(result.Symbol.Documentation.Summary);
	}

	[Fact]
	public async Task WhenTheNameNearlyMatches_ThenRankedCandidatesAreSuggested()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greet");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
		Assert.Contains("SimpleLibrary.Greeter", result.Error.Candidates!);
	}

	[Fact]
	public async Task WhenAMetadataTypeIsRequested_ThenItResolvesFromTheReferencedAssembly()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "System.String");

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.Symbol);
		Assert.Equal("String", result.Symbol!.Name);
		Assert.Equal("metadata", result.Symbol.SourceType);
		Assert.False(string.IsNullOrEmpty(result.Symbol.Assembly));
		Assert.Null(result.Symbol.SourcePath);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResult result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
