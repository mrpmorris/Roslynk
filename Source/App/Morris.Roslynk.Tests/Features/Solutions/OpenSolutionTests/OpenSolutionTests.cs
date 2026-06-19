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

		Assert.Contains("#status=Building", opening);
		Assert.Contains("#projects=0", opening);

		await registry.GetOrAddAsync(TestSolutions.Simple);
		string ready = subject.OpenSolution(TestSolutions.Simple);

		Assert.DoesNotContain("#status", ready);
		Assert.Contains("#projects=1", ready);
		Assert.Contains("SimpleLibrary.csproj,", ready);
	}
}
