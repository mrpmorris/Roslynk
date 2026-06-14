using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.InstanceRegistryTests;

public class GetOrAddAsyncTests
{
	[Fact]
	public async Task WhenTheSameSolutionIsRequestedTwice_ThenTheSameInstanceIsShared()
	{
		using var subject = new InstanceRegistry();

		RoslynInstance first = await subject.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance second = await subject.GetOrAddAsync(TestSolutions.Simple);

		Assert.Same(first, second);
	}
}
