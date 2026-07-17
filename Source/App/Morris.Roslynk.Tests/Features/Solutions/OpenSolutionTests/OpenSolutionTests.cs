using Morris.Roslynk.Features.Solutions.OpenSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.OpenSolutionTests;

public class OpenSolutionTests
{
	[Fact]
	public async Task WhenOpeningASolution_ThenItReturnsImmediatelyAndBuildsInTheBackground()
	{
		using var registry = new InstanceRegistry();
		var subject = new OpenSolutionTool(registry);

		string opening = subject.OpenSolution(TestSolutions.Simple);

		Assert.Contains("status=Building", opening);
		Assert.Contains("projects=0", opening);

		await registry.GetOrAddAsync(TestSolutions.Simple);
		string ready = subject.OpenSolution(TestSolutions.Simple);

		Assert.DoesNotContain("status", ready);
		Assert.Contains("projects=1", ready);
		Assert.Contains("SimpleLibrary.csproj,", ready);
	}

	[Fact]
	public void WhenOpeningASolutionThatDoesNotExist_ThenItReturnsNotFoundImmediately()
	{
		using var registry = new InstanceRegistry();
		var subject = new OpenSolutionTool(registry);

		string nonexistentPath = Path.Combine(Path.GetTempPath(), "DoesNotExist", "Solution.slnx");
		string result = subject.OpenSolution(nonexistentPath);

		Assert.Contains("error=NotFound", result);
		Assert.Contains("No solution file was found at", result);
		Assert.Empty(registry.OpenSolutionPaths);
	}
}
