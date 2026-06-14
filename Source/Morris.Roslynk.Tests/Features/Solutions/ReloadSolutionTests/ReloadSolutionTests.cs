using Morris.Roslynk.Features.Solutions.ReloadSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.ReloadSolutionTests;

public class ReloadSolutionTests
{
	[Fact]
	public async Task WhenReloadingASolution_ThenThePreviousSnapshotIsServedWhileItRebuilds()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ReloadSolutionTool(registry);

		ReloadSolutionResult result = subject.ReloadSolution(TestSolutions.Simple);

		Assert.Equal(SolutionStatus.Building, result.Status);
		Assert.True(result.ProjectCount >= 1);

		await WaitForReadyAsync(registry, TestSolutions.Simple);
	}

	private static async Task WaitForReadyAsync(InstanceRegistry registry, string solutionPath)
	{
		RoslynInstance instance = registry.GetOrBegin(solutionPath);
		DateTime deadline = DateTime.UtcNow.AddSeconds(60);
		while (instance.CurrentModel.Status != SolutionStatus.Ready)
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException("The reload did not complete in time.");
			await Task.Delay(25);
		}
	}
}
