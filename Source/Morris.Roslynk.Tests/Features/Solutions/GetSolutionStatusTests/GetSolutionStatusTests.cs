using Morris.Roslynk.Features.Solutions.GetSolutionStatus;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.GetSolutionStatusTests;

public class GetSolutionStatusTests
{
	[Fact]
	public async Task WhenASolutionIsLoaded_ThenItIsReadyWithLoadedMatchingTotal()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		GetSolutionStatusResponse response = subject.GetSolutionStatus();

		LoadedSolutionStatus loaded = Assert.Single(response.Solutions);
		Assert.Equal(SolutionStatus.Ready, loaded.Status);
		Assert.False(string.IsNullOrEmpty(loaded.SnapshotId));
		Assert.Equal(1, loaded.TotalProjects);
		Assert.Equal(loaded.TotalProjects, loaded.LoadedProjects);
	}

	[Fact]
	public async Task WhenAMultiTargetedSolutionIsLoaded_ThenEachTargetFrameworkCountsAsAProject()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSolutionStatusTool(registry);

		GetSolutionStatusResponse response = subject.GetSolutionStatus();

		LoadedSolutionStatus loaded = Assert.Single(response.Solutions);
		Assert.Equal(SolutionStatus.Ready, loaded.Status);
		Assert.Equal(2, loaded.TotalProjects);
		Assert.Equal(2, loaded.LoadedProjects);
	}

	[Fact]
	public async Task WhileASolutionIsStillLoading_ThenTheTotalIsUnknownAndStatusIsBuilding()
	{
		using var registry = new InstanceRegistry();
		registry.GetOrBegin(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		GetSolutionStatusResponse response = subject.GetSolutionStatus();

		LoadedSolutionStatus loaded = Assert.Single(response.Solutions);
		Assert.Equal(SolutionStatus.Building, loaded.Status);
		Assert.Null(loaded.TotalProjects);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
