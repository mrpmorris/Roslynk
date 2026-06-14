using Morris.Roslynk.Features.Solutions.ReloadSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.ReloadSolutionTests;

public class ReloadSolutionTests
{
	[Fact]
	public async Task WhenReloadingASolution_ThenItIsLoadedFreshWithItsProjects()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ReloadSolutionTool(registry);

		ReloadSolutionResponse response = await subject.ReloadSolution(TestSolutions.Simple);

		Assert.True(response.ProjectCount >= 1);
	}
}
