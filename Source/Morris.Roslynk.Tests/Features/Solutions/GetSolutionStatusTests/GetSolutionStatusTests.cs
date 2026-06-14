using Morris.Roslynk.Features.Solutions.GetSolutionStatus;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.GetSolutionStatusTests;

public class GetSolutionStatusTests
{
	[Fact]
	public async Task WhenASolutionIsLoaded_ThenItAppearsWithItsReadyStatusSnapshotAndProjects()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		GetSolutionStatusResponse response = subject.GetSolutionStatus();

		LoadedSolutionStatus loaded = Assert.Single(response.Solutions);
		Assert.Equal(SolutionStatus.Ready, loaded.Status);
		Assert.False(string.IsNullOrEmpty(loaded.SnapshotId));
		Assert.True(loaded.ProjectCount >= 1);
	}
}
