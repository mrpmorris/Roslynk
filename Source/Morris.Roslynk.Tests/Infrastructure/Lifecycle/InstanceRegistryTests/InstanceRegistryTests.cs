using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.InstanceRegistryTests;

public class InstanceRegistryTests
{
	[Fact]
	public async Task WhenAnInstanceIsNotIdleLongEnough_ThenItIsNotEvicted()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);

		int evicted = registry.EvictIdle(TimeSpan.FromHours(1), DateTime.UtcNow);

		Assert.Equal(0, evicted);
		Assert.Single(registry.LoadedInstances());
	}

	[Fact]
	public async Task WhenAnInstanceHasBeenIdle_ThenItIsEvicted()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);

		int evicted = registry.EvictIdle(TimeSpan.Zero, DateTime.UtcNow.AddDays(1));

		Assert.Equal(1, evicted);
		Assert.Empty(registry.LoadedInstances());
	}
}
