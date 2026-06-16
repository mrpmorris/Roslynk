using Morris.Roslynk.Features.Solutions.CloseSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.CloseSolutionTests;

public class CloseSolutionTests
{
	[Fact]
	public async Task WhenClosingAnOpenSolution_ThenItIsUnloadedAndTheResultIsEmpty()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new CloseSolutionTool(registry);

		string result = subject.CloseSolution(TestSolutions.Simple);

		Assert.Equal("", result);
		Assert.Empty(registry.LoadedInstances());
	}
}
