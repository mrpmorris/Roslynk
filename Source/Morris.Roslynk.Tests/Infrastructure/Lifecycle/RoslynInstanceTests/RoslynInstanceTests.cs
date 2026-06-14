using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.RoslynInstanceTests;

public class RoslynInstanceTests
{
	[Fact]
	public async Task WhileTheInitialLoadIsInFlight_ThenTheModelIsBuildingWithNoSnapshot()
	{
		var gate = new TaskCompletionSource();
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));

		subject.BeginInitialLoad(
			loader: async () =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple);
			},
			onReady: _ => { });

		Assert.Equal(SolutionStatus.Building, subject.CurrentModel.Status);
		Assert.Null(subject.CurrentModel.Solution);

		gate.SetResult();
		await subject.WaitUntilReadyAsync();

		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
		Assert.NotNull(subject.CurrentModel.Solution);
	}

	[Fact]
	public async Task WhenAdvanced_ThenTheSnapshotIdChangesAndStatusStaysReady()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		string before = subject.CurrentModel.SnapshotId;

		subject.AdvanceTo(subject.CurrentSolution);

		Assert.NotEqual(before, subject.CurrentModel.SnapshotId);
		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
	}

	[Fact]
	public async Task WhileRebuilding_ThenThePreviousSnapshotIsStillServedAsBuilding()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		Solution previous = subject.CurrentSolution;
		var gate = new TaskCompletionSource();

		subject.BeginRebuild(
			loader: async () =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple);
			},
			onReady: _ => { });

		Assert.Equal(SolutionStatus.Building, subject.CurrentModel.Status);
		Assert.Same(previous, subject.CurrentModel.Solution);

		gate.SetResult();
		await WaitForReadyAsync(subject);

		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
		Assert.NotSame(previous, subject.CurrentModel.Solution);
	}

	private static async Task<RoslynInstance> LoadReadyAsync()
	{
		var instance = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		instance.BeginInitialLoad(() => SolutionWorkspace.LoadAsync(TestSolutions.Simple), _ => { });
		await instance.WaitUntilReadyAsync();
		return instance;
	}

	private static async Task WaitForReadyAsync(RoslynInstance instance)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(60);
		while (instance.CurrentModel.Status != SolutionStatus.Ready)
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException("The rebuild did not complete in time.");
			await Task.Delay(25);
		}
	}
}
