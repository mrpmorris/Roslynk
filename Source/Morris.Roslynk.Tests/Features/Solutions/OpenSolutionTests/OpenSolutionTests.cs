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

		OpenSolutionResult opening = subject.OpenSolution(TestSolutions.Simple);

		Assert.Equal(SolutionStatus.Building, opening.Status);
		Assert.Empty(opening.Projects!);

		await registry.GetOrAddAsync(TestSolutions.Simple);
		OpenSolutionResult ready = subject.OpenSolution(TestSolutions.Simple);

		Assert.Equal(SolutionStatus.Ready, ready.Status);
		OpenSolutionProject project = Assert.Single(ready.Projects!);
		Assert.Equal("SimpleLibrary", project.Name);
	}
}
