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

		string result = subject.GetSolutionStatus();

		Assert.Contains("#count=1", result);
		// One line "<solutionId>,Ready,1/1,<snapshot>".
		string line = result.Split('\n').First(candidate => candidate.Contains(",Ready,", StringComparison.Ordinal));
		Assert.Contains(",1/1,", line);
	}

	[Fact]
	public async Task WhenAMultiTargetedSolutionIsLoaded_ThenEachTargetFrameworkCountsAsAProject()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		string line = result.Split('\n').First(candidate => candidate.Contains(",Ready,", StringComparison.Ordinal));
		Assert.Contains(",2/2,", line);
	}

	[Fact]
	public async Task WhileASolutionIsStillLoading_ThenTheTotalIsUnknownAndStatusIsBuilding()
	{
		using var registry = new InstanceRegistry();
		registry.GetOrBegin(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.Contains("#count=1", result);
		Assert.Contains(",Building,", result);
		Assert.Contains("/?,", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
