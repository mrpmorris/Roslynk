using Morris.Roslynk.Features.Build.BuildSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Build.BuildSolutionTests;

public class BuildSolutionTests
{
	[Fact]
	public async Task WhenBuildingACleanSolution_ThenItSucceedsWithNoErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new BuildSolutionTool(registry);

		BuildSolutionResult result = await subject.BuildSolution(TestSolutions.Simple);

		Assert.True(result.IsSuccess);
		Assert.True(result.Succeeded);
		Assert.Equal(0, result.Errors!.Value);
	}

	[Fact]
	public async Task WhenBuildingABrokenSolution_ThenItFailsWithErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new BuildSolutionTool(registry);

		BuildSolutionResult result = await subject.BuildSolution(TestSolutions.Broken);

		Assert.True(result.IsSuccess);
		Assert.False(result.Succeeded);
		Assert.True(result.Errors!.Value >= 1);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new BuildSolutionTool(registry);

		BuildSolutionResult result = await subject.BuildSolution(TestSolutions.Simple);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
