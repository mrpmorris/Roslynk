using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCallersTests;

public class GetCallersDisposeTests
{
	[Fact]
	public async Task WhenADisposableLocalIsDeclaredWithoutUsing_ThenTheDeclaringMethodIsNotACaller()
	{
		string result = await CallersAsync("DisposeLib.Resource.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,DeclaresWithoutUsing", result);
	}

	[Fact]
	public async Task WhenAUsingDeclarationDisposes_ThenTheUsingMethodIsACaller()
	{
		string result = await CallersAsync("DisposeLib.Resource.Dispose");

		Assert.Contains("method,UsesUsing,", result);
	}

	[Fact]
	public async Task WhenAMethodBothDeclaresAPlainLocalAndUsesARealUsing_ThenItIsStillACaller()
	{
		string result = await CallersAsync("DisposeLib.Resource.Dispose");

		Assert.Contains("method,DeclaresAndUsesUsing,", result);
	}

	[Fact]
	public async Task WhenAnAwaitUsingDeclarationDisposesAsync_ThenTheMethodIsACaller()
	{
		string result = await CallersAsync("DisposeLib.AsyncResource.DisposeAsync");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,UsesAwaitUsingAsync,", result);
	}

	[Fact]
	public async Task WhenANonInterfaceDisposeIsOnlyMatchedByAPlainLocal_ThenItHasNoCallers()
	{
		string result = await CallersAsync("DisposeLib.PlainResource.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,DeclaresPlainWithoutUsing", result);
	}

	[Fact]
	public async Task WhenANonInterfaceDisposeIsCalledExplicitly_ThenTheCallingMethodIsACaller()
	{
		string result = await CallersAsync("DisposeLib.CalledResource.Dispose");

		Assert.Contains("method,CallsCalledDispose,", result);
	}

	private static async Task<string> CallersAsync(string symbolName)
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Dispose);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetCallers(TestSolutions.Dispose, symbolName);
	}
}
