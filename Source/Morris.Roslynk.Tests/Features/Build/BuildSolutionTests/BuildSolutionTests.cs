using Morris.Roslynk.Features.Build.BuildSolution;

namespace Morris.Roslynk.Tests.Features.Build.BuildSolutionTests;

public class BuildSolutionTests
{
	[Fact]
	public async Task WhenBuildingACleanSolution_ThenItSucceedsWithNoErrors()
	{
		var subject = new BuildSolutionTool();

		BuildSolutionResponse response = await subject.BuildSolution(TestSolutions.Simple);

		Assert.True(response.Succeeded);
		Assert.Equal(0, response.Errors);
	}

	[Fact]
	public async Task WhenBuildingABrokenSolution_ThenItFailsWithErrors()
	{
		var subject = new BuildSolutionTool();

		BuildSolutionResponse response = await subject.BuildSolution(TestSolutions.Broken);

		Assert.False(response.Succeeded);
		Assert.True(response.Errors >= 1);
	}
}
