using Morris.Roslynk.Features.Symbols.GetTypeHierarchy;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetTypeHierarchyTests;

public class GetTypeHierarchyTests
{
	[Fact]
	public async Task WhenATypeImplementsAnInterface_ThenTheInterfaceIsInTheHierarchy()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("resolvedType=SimpleLibrary.Greeter", result);
		Assert.Contains("interfaces\n", result);
		Assert.Contains("interface,SimpleLibrary.IGreeter", result);
	}

	[Fact]
	public async Task WhenASectionIsEmpty_ThenItIsOmitted()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		// Greeter has no derived types, so the 'derived' section header is absent entirely.
		Assert.DoesNotContain("derived", result);
	}

	[Fact]
	public async Task WhenTheTypeIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetTypeHierarchy(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
