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

		string result = subject.ClearCache();

		Assert.DoesNotContain("#cleared=0", result);
		Assert.StartsWith("#cleared=", result);
		Assert.Empty(registry.LoadedInstances());
	}
}
