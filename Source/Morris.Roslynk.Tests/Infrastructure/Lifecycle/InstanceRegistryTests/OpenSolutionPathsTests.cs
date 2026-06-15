using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.InstanceRegistryTests;

public class OpenSolutionPathsTests
{
	[Fact]
	public void WhenNoSolutionsAreOpen_ThenItIsEmpty()
	{
		using var subject = new InstanceRegistry();

		Assert.Empty(subject.OpenSolutionPaths);
	}

	[Fact]
	public async Task WhenASolutionIsOpen_ThenItsPathIsListed()
	{
		using var subject = new InstanceRegistry();
		await subject.GetOrAddAsync(TestSolutions.Simple);

		string path = Assert.Single(subject.OpenSolutionPaths);
		Assert.Contains("SimpleSolution", path);
	}

	[Fact]
	public async Task WhenAnOpenSolutionIsClosed_ThenItIsRemoved()
	{
		using var subject = new InstanceRegistry();
		await subject.GetOrAddAsync(TestSolutions.Simple);

		subject.TryClose(TestSolutions.Simple);

		Assert.Empty(subject.OpenSolutionPaths);
	}
}
