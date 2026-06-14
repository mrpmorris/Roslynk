using Morris.Roslynk.Features.Symbols.GetTypeHierarchy;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetTypeHierarchyTests;

public class GetTypeHierarchyTests
{
	[Fact]
	public async Task WhenATypeImplementsAnInterface_ThenTheInterfaceIsInTheHierarchy()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver());

		TypeHierarchyResponse response = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Equal("SimpleLibrary.Greeter", response.ResolvedType);
		Assert.Contains("SimpleLibrary.IGreeter", response.Interfaces);
	}
}
