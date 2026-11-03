using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins.SolutionProviderTests;

public class GetModelAsyncTests
{
	[Fact]
	public async Task WhenTheSolutionIsNotOpen_ThenItIsOpenedAndItsReadyModelReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new SolutionProvider(registry);

		GetModelResponse response = await subject.GetModelAsync(TestSolutions.Simple);

		Assert.Equal(SolutionStatus.Ready, response.Status);
		Assert.NotEqual(Guid.Empty, response.ModelId);
		Assert.Null(response.ErrorMessage);
		Assert.Equal("SimpleLibrary", Assert.Single(response.Model!.Projects).Name);
		Assert.Contains(Path.GetFullPath(TestSolutions.Simple), registry.OpenSolutionPaths);
	}

	[Fact]
	public async Task WhenTheSolutionIsAlreadyOpen_ThenItsCurrentModelIsReturned()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SolutionProvider(registry);

		GetModelResponse response = await subject.GetModelAsync(TestSolutions.Simple);

		Assert.Equal(instance.CurrentModel.Id, response.ModelId);
		Assert.Same(instance.CurrentSolution, response.Model);
	}

	[Fact]
	public async Task WhenTheSolutionFileIsMissing_ThenTheResponseIsFaultedWithTheReason()
	{
		using var registry = new InstanceRegistry();
		var subject = new SolutionProvider(registry);
		string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.slnx");

		GetModelResponse response = await subject.GetModelAsync(missing);

		Assert.Equal(SolutionStatus.Faulted, response.Status);
		Assert.Null(response.Model);
		Assert.Contains("No solution file", response.ErrorMessage);
		Assert.Empty(registry.OpenSolutionPaths);
	}

	[Fact]
	public async Task WhenAPluginReadsASolution_ThenItCountsAsUseForIdleEviction()
	{
		using var registry = new InstanceRegistry();
		var subject = new SolutionProvider(registry);
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		DateTime openedAt = instance.LastAccessedUtc;
		await Task.Delay(TimeSpan.FromMilliseconds(20));

		await subject.GetModelAsync(TestSolutions.Simple);

		Assert.True(instance.LastAccessedUtc > openedAt);
	}
}
