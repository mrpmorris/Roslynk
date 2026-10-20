using Morris.Roslynk.Features.Solutions.OpenSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.OpenSolutionTests;

public class OpenSolutionTests
{
	[Fact]
	public async Task WhenOpeningASolutionWithNoWait_ThenItReturnsImmediatelyAndBuildsInTheBackground()
	{
		using var registry = new InstanceRegistry();
		var subject = new OpenSolutionTool(registry);

		string opening = await subject.OpenSolution(TestSolutions.Simple, noWait: true);

		Assert.Contains("status=Building", opening);
		Assert.Contains("projects=0", opening);

		await registry.GetOrAddAsync(TestSolutions.Simple);
		string ready = await subject.OpenSolution(TestSolutions.Simple);

		Assert.DoesNotContain("status", ready);
		Assert.Contains("projects=1", ready);
		Assert.Contains("SimpleLibrary.csproj,", ready);
	}

	[Fact]
	public async Task WhenOpeningASolution_ThenItWaitsForTheLoadAndReturnsTheReadySnapshot()
	{
		using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.FromMinutes(5)));
		var subject = new OpenSolutionTool(registry);

		string result = await subject.OpenSolution(TestSolutions.Simple);

		Assert.DoesNotContain("status", result);
		Assert.Contains("projects=1", result);
		Assert.Contains("SimpleLibrary.csproj,", result);
	}

	[Fact]
	public async Task WhenTheLoadExceedsTheWaitTimeout_ThenOpeningReturnsBuildingImmediately()
	{
		using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));
		var subject = new OpenSolutionTool(registry);

		string result = await subject.OpenSolution(TestSolutions.Simple);

		Assert.Contains("status=Building", result);
		Assert.Contains("projects=0", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenOpeningASolutionThatDoesNotExist_ThenItReturnsNotFoundImmediately()
	{
		using var registry = new InstanceRegistry();
		var subject = new OpenSolutionTool(registry);

		string nonexistentPath = Path.Combine(Path.GetTempPath(), "DoesNotExist", "Solution.slnx");
		string result = await subject.OpenSolution(nonexistentPath);

		Assert.Contains("error=NotFound", result);
		Assert.Contains("No solution file was found at", result);
		Assert.Empty(registry.OpenSolutionPaths);
	}
}
