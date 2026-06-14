using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class FindReferencesTests
{
	[Fact]
	public async Task WhenAReferencedTypeIsRequested_ThenItsReferencesAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResponse response = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.NotNull(response.ResolvedSymbol);
		Assert.NotEmpty(response.References);
	}

	[Fact]
	public async Task WhenTheSymbolIsNotFound_ThenNoReferencesAndNoCandidates()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		FindReferencesResponse response = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Null(response.ResolvedSymbol);
		Assert.Empty(response.References);
		Assert.Empty(response.Candidates);
	}
}
