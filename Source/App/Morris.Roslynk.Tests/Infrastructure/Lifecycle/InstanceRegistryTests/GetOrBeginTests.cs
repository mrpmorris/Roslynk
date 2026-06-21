using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.InstanceRegistryTests;

public class GetOrBeginTests
{
	[Fact]
	public void WhenTheSameSolutionIsRequestedTwice_ThenTheSameInstanceIsShared()
	{
		using var subject = new InstanceRegistry();

		RoslynInstance first = subject.GetOrBegin(TestSolutions.Simple);
		RoslynInstance second = subject.GetOrBegin(TestSolutions.Simple);

		Assert.Same(first, second);
	}

	[Fact]
	public async Task WhenADirtyInstanceIsRequested_ThenItIsRebuiltAndPicksUpTheDiskChange()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var subject = new InstanceRegistry();
		RoslynInstance instance = await subject.GetOrAddAsync(solutionPath);

		// Mimic a build-file / additional-document edit: change a source file on disk and mark the snapshot
		// dirty (what SolutionFileSync does for a change it cannot fold incrementally, e.g. a .mixin edit).
		string greeter = Directory
			.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories)
			.First();
		await File.WriteAllTextAsync(greeter, (await File.ReadAllTextAsync(greeter)) + "\n// rebuilt from disk\n");
		instance.MarkDirty();

		// The non-blocking read path serves the stale snapshot while a background rebuild runs.
		RoslynInstance same = subject.GetOrBegin(solutionPath);
		Assert.Same(instance, same);
		await WaitForReadyAsync(instance);

		Assert.False(instance.IsDirty);
		DocumentId id = instance.CurrentSolution.GetDocumentIdsWithFilePath(greeter).First();
		string text = (await instance.CurrentSolution.GetDocument(id)!.GetTextAsync()).ToString();
		Assert.Contains("rebuilt from disk", text);
	}

	[Fact]
	public async Task WhenACleanInstanceIsRequested_ThenTheSnapshotIsNotReplaced()
	{
		using var subject = new InstanceRegistry();
		RoslynInstance instance = await subject.GetOrAddAsync(TestSolutions.Simple);
		Solution before = instance.CurrentSolution;

		subject.GetOrBegin(TestSolutions.Simple);

		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenADirtyInstanceIsRequestedViaGetOrBeginAsync_ThenOneCallReturnsTheRebuiltSnapshot()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var subject = new InstanceRegistry();
		RoslynInstance instance = await subject.GetOrAddAsync(solutionPath);

		// Mimic an additional-document edit (e.g. a .mixin): change a source file on disk and mark dirty.
		string greeter = Directory
			.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories)
			.First();
		await File.WriteAllTextAsync(greeter, (await File.ReadAllTextAsync(greeter)) + "\n// rebuilt in one call\n");
		instance.MarkDirty();

		// The blocking read path awaits the rebuild, so a single call returns a Ready, up-to-date snapshot.
		RoslynInstance same = await subject.GetOrBeginAsync(solutionPath);

		Assert.Same(instance, same);
		Assert.Equal(SolutionStatus.Ready, instance.CurrentModel.Status);
		Assert.False(instance.IsDirty);
		DocumentId id = instance.CurrentSolution.GetDocumentIdsWithFilePath(greeter).First();
		string text = (await instance.CurrentSolution.GetDocument(id)!.GetTextAsync()).ToString();
		Assert.Contains("rebuilt in one call", text);
	}

	[Fact]
	public async Task WhenACleanInstanceIsRequestedViaGetOrBeginAsync_ThenTheSnapshotIsNotReplaced()
	{
		using var subject = new InstanceRegistry();
		RoslynInstance instance = await subject.GetOrAddAsync(TestSolutions.Simple);
		Solution before = instance.CurrentSolution;

		RoslynInstance same = await subject.GetOrBeginAsync(TestSolutions.Simple);

		Assert.Same(instance, same);
		Assert.Same(before, instance.CurrentSolution);
		Assert.Equal(SolutionStatus.Ready, instance.CurrentModel.Status);
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
