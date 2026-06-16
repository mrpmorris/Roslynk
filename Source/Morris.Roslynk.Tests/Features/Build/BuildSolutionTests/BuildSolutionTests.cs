using Morris.Roslynk.Features.Build.BuildSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Build.BuildSolutionTests;

public class BuildSolutionTests
{
	[Fact]
	public async Task WhenBuildingACleanSolution_ThenItSucceedsWithNoErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new BuildSolutionTool(registry);

		string result = await subject.BuildSolution(TestSolutions.Simple);

		Assert.Contains("#succeeded=true", result);
		Assert.Contains("#errors=0", result);
	}

	[Fact]
	public async Task WhenBuildingABrokenSolution_ThenItFailsWithErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new BuildSolutionTool(registry);

		string result = await subject.BuildSolution(TestSolutions.Broken);

		Assert.Contains("#succeeded=false", result);
		Assert.DoesNotContain("#errors=0", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new BuildSolutionTool(registry);

		string result = await subject.BuildSolution(TestSolutions.Simple);

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
