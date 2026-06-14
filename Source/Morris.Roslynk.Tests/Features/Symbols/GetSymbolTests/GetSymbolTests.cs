using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolTests;

public class GetSymbolTests
{
	[Fact]
	public async Task WhenAnExistingTypeIsRequested_ThenItsDetailsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResponse response = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.NotNull(response.Symbol);
		Assert.Equal("Greeter", response.Symbol!.Name);
		Assert.Equal("NamedType", response.Symbol.Kind);
		Assert.EndsWith("Greeter.cs", response.Symbol.SourcePath);
	}

	[Fact]
	public async Task WhenTheSymbolDoesNotExist_ThenSymbolIsNull()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResponse response = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Null(response.Symbol);
		Assert.Empty(response.Candidates);
	}

	[Fact]
	public async Task WhenTheSymbolHasDocumentation_ThenItIsIncluded()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		GetSymbolResponse response = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");

		Assert.NotNull(response.Symbol);
		Assert.Equal("own", response.Symbol!.Documentation.Source);
		Assert.NotNull(response.Symbol.Documentation.Summary);
	}
}
