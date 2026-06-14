using Morris.Roslynk.Features.Solutions.CloseSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.CloseSolutionTests;

public class CloseSolutionTests
{
	[Fact]
	public async Task WhenClosingAnOpenSolution_ThenItIsClosedAndClosingAgainReportsFalse()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new CloseSolutionTool(registry);

		CloseSolutionResponse first = subject.CloseSolution(TestSolutions.Simple);
		CloseSolutionResponse second = subject.CloseSolution(TestSolutions.Simple);

		Assert.True(first.Closed);
		Assert.False(second.Closed);
	}
}
