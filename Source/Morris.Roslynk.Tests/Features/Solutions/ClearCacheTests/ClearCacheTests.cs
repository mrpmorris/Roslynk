using Morris.Roslynk.Features.Solutions.ClearCache;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.ClearCacheTests;

public class ClearCacheTests
{
	[Fact]
	public async Task WhenClearingTheCache_ThenLoadedSolutionsAreClosed()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ClearCacheTool(registry);

		ClearCacheResponse response = subject.ClearCache();

		Assert.True(response.Closed >= 1);
		Assert.Empty(registry.LoadedInstances());
	}
}
