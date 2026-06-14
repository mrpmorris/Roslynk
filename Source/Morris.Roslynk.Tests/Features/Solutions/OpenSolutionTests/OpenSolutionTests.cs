using Morris.Roslynk.Features.Solutions.OpenSolution;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Solutions.OpenSolutionTests;

public class OpenSolutionTests
{
	[Fact]
	public async Task WhenOpeningASolution_ThenItsProjectsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new OpenSolutionTool(registry);

		OpenSolutionResponse response = await subject.OpenSolution(TestSolutions.Simple);

		OpenSolutionProject project = Assert.Single(response.Projects);
		Assert.Equal("SimpleLibrary", project.Name);
	}
}
