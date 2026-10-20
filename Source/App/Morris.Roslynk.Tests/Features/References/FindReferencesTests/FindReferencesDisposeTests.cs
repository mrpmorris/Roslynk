using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class FindReferencesDisposeTests
{
	[Fact]
	public async Task WhenADisposableLocalIsDeclaredWithoutUsing_ThenTheDeclaringMethodIsNotADisposeReference()
	{
		string result = await FindAsync("DisposeLib.Resource.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,DeclaresWithoutUsing", result);
	}

	[Fact]
	public async Task WhenADisposableLocalIsDeclaredWithoutUsing_ThenARealUsingAndAnExplicitCallAreReferences()
	{
		string result = await FindAsync("DisposeLib.Resource.Dispose");

		Assert.Contains("method,UsesUsing,", result);
		Assert.Contains("method,CallsDispose,", result);
	}

	[Fact]
	public async Task WhenAMethodBothDeclaresAPlainLocalAndUsesARealUsing_ThenItIsStillAReference()
	{
		string result = await FindAsync("DisposeLib.Resource.Dispose");

		Assert.Contains("method,DeclaresAndUsesUsing,", result);
	}

	[Fact]
	public async Task WhenAnAwaitUsingDeclarationDisposesAsync_ThenRoslynReportsNoSourceReference()
	{
		string result = await FindAsync("DisposeLib.AsyncResource.DisposeAsync");

		// Pre-existing Roslyn behavior, unrelated to this fix and verified against the unfixed base commit:
		// SymbolFinder.FindReferencesAsync reports an await using's DisposeAsync against the IAsyncDisposable
		// interface member, so the implementation gets no source reference. get_callers still reports the
		// method through FindCallersAsync, and the spurious-local filter never drops a 'using' location.
		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,UsesAwaitUsingAsync", result);
	}

	[Fact]
	public async Task WhenANonInterfaceDisposeIsOnlyMatchedByAPlainLocal_ThenItHasNoReferences()
	{
		string result = await FindAsync("DisposeLib.PlainResource.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,", result);
	}

	[Fact]
	public async Task WhenANonInterfaceDisposeIsCalledExplicitly_ThenTheCallingMethodIsAReference()
	{
		string result = await FindAsync("DisposeLib.CalledResource.Dispose");

		Assert.Contains("method,CallsCalledDispose,", result);
	}

	private static async Task<string> FindAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Dispose);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.FindReferences(TestSolutions.Dispose, symbolName);
	}
}
