using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
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
			loader: async progress =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
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
	public async Task WhenAdvanced_ThenTheNewSolutionIsPublishedAndStatusStaysReady()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		Solution advanced = subject.CurrentSolution;

		subject.AdvanceTo(advanced);

		Assert.Same(advanced, subject.CurrentModel.Solution);
		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
	}

	[Fact]
	public async Task WhileRebuilding_ThenThePreviousSnapshotIsStillServedAsBuilding()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		Solution previous = subject.CurrentSolution;
		var gate = new TaskCompletionSource();

		subject.BeginRebuild(
			loader: async progress =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
			},
			onReady: _ => { });

		Assert.Equal(SolutionStatus.Building, subject.CurrentModel.Status);
		Assert.Same(previous, subject.CurrentModel.Solution);

		gate.SetResult();
		await WaitForReadyAsync(subject);

		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
		Assert.NotSame(previous, subject.CurrentModel.Solution);
	}

	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task WhenWritesAreEnqueued_ThenTheyApplyInOrder()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		var order = new List<int>();
		var writes = new List<Task>();
		for (int index = 0; index < 5; index++)
		{
			int captured = index;
			writes.Add(subject.EnqueueWriteAsync((current, token) =>
			{
				lock (order)
					order.Add(captured);
				return Task.FromResult(new WriteResult(current, []));
			}));
		}

		await Task.WhenAll(writes).WaitAsync(Timeout);
		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
	}

	[Fact]
	public async Task WhenAReadStartsDuringAWrite_ThenItWaitsForTheWriteToPublish()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		var started = new TaskCompletionSource();
		var release = new TaskCompletionSource();
		Task write = subject.EnqueueWriteAsync(async (current, token) =>
		{
			started.SetResult();
			await release.Task;
			return new WriteResult(current, []);
		});

		await started.Task.WaitAsync(Timeout);
		Task<SolutionModel> read = subject.ReadModelAsync();
		await Task.Delay(50);
		Assert.False(read.IsCompleted);

		release.SetResult();
		await write.WaitAsync(Timeout);
		SolutionModel model = await read.WaitAsync(Timeout);
		Assert.Equal(SolutionStatus.Ready, model.Status);
	}

	[Fact]
	public async Task WhenADiagnosticsBuildIsQueuedAfterWrites_ThenTheWritesDrainFirst()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		var events = new List<string>();
		Task write = subject.EnqueueWriteAsync((current, token) =>
		{
			lock (events)
				events.Add("write");
			return Task.FromResult(new WriteResult(current, []));
		});
		Task build = subject.RequestDiagnosticsAsync("key", (solution, token) =>
		{
			lock (events)
				events.Add("build");
			return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
		});

		await Task.WhenAll(write, build).WaitAsync(Timeout);
		Assert.Equal(new[] { "write", "build" }, events);
	}

	[Fact]
	public async Task WhenNothingChangedSinceTheLastBuild_ThenTheCachedDiagnosticsAreReused()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		int compiles = 0;
		Task<IReadOnlyList<Diagnostic>> Compute(Solution solution, CancellationToken token)
		{
			Interlocked.Increment(ref compiles);
			return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
		}

		await subject.RequestDiagnosticsAsync("key", Compute).WaitAsync(Timeout);
		await subject.RequestDiagnosticsAsync("key", Compute).WaitAsync(Timeout);
		Assert.Equal(1, compiles);

		await subject.EnqueueWriteAsync((current, token) =>
		{
			DocumentId documentId = current.Projects.First().DocumentIds[0];
			return Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// edited")), []));
		}).WaitAsync(Timeout);
		await subject.RequestDiagnosticsAsync("key", Compute).WaitAsync(Timeout);
		Assert.Equal(2, compiles);
	}

	[Fact]
	public async Task WhenAWriteChangesNothing_ThenTheCachedDiagnosticsAreStillReused()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		int compiles = 0;
		Task<IReadOnlyList<Diagnostic>> Compute(Solution solution, CancellationToken token)
		{
			Interlocked.Increment(ref compiles);
			return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
		}

		await subject.RequestDiagnosticsAsync("key", Compute).WaitAsync(Timeout);

		// A watcher event for content the snapshot already holds returns the snapshot unchanged.
		await subject.EnqueueWriteAsync((current, token) => Task.FromResult(new WriteResult(current, []))).WaitAsync(Timeout);
		await subject.RequestDiagnosticsAsync("key", Compute).WaitAsync(Timeout);

		Assert.Equal(1, compiles);
		Assert.False(subject.BuildNeeded);
	}

	[Fact]
	public async Task WhenAWriteTransformThrows_ThenLaterWorkStillRuns()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		Task faulting = subject.EnqueueWriteAsync((current, token) => throw new InvalidOperationException("boom"));
		await Assert.ThrowsAsync<InvalidOperationException>(() => faulting);

		IReadOnlyList<string> changed = await subject
			.EnqueueWriteAsync((current, token) => Task.FromResult(new WriteResult(current, ["after"])))
			.WaitAsync(Timeout);
		Assert.Equal(new[] { "after" }, changed);
	}

	[Fact]
	public async Task WhenDisposed_ThenFurtherWritesFault()
	{
		RoslynInstance subject = await LoadReadyAsync();
		subject.Dispose();

		await Assert.ThrowsAnyAsync<Exception>(() =>
			subject.EnqueueWriteAsync((current, token) => Task.FromResult(new WriteResult(current, []))));
	}
	[Fact]
	public async Task WhenTheInitialLoadExceedsTheWaitTimeout_ThenTheWaitReturnsFalseWhileTheModelStaysBuilding()
	{
		var gate = new TaskCompletionSource();
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		subject.BeginInitialLoad(
			loader: async progress =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
			},
			onReady: _ => { });

		bool loaded = await subject.WaitForInitialLoadAsync(TimeSpan.Zero);

		Assert.False(loaded);
		Assert.Equal(SolutionStatus.Building, subject.CurrentModel.Status);
		Assert.Null(subject.CurrentModel.Solution);

		gate.SetResult();
		await subject.WaitUntilReadyAsync();
		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
	}

	[Fact]
	public async Task WhenTwoCallersWaitForTheSameInitialLoad_ThenTheyJoinThatOneLoad()
	{
		var gate = new TaskCompletionSource();
		int loads = 0;
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		subject.BeginInitialLoad(
			loader: async progress =>
			{
				Interlocked.Increment(ref loads);
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
			},
			onReady: _ => { });

		Task<bool> first = subject.WaitForInitialLoadAsync(TimeSpan.FromMinutes(5));
		Task<bool> second = subject.WaitForInitialLoadAsync(TimeSpan.FromMinutes(5));
		gate.SetResult();

		bool[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(5));
		Assert.All(results, Assert.True);
		Assert.Equal(1, loads);
		Assert.Equal(SolutionStatus.Ready, subject.CurrentModel.Status);
	}

	[Fact]
	public async Task WhenTheWaitIsCancelled_ThenItStopsWaitingWithoutStartingAnotherLoad()
	{
		var gate = new TaskCompletionSource();
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		subject.BeginInitialLoad(
			loader: async progress =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
			},
			onReady: _ => { });
		using var cancellation = new CancellationTokenSource();
		Task<bool> waiting = subject.WaitForInitialLoadAsync(Timeout, cancellation.Token);

		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
		gate.SetResult();
		await subject.WaitUntilReadyAsync();
	}

	private static async Task<RoslynInstance> LoadReadyAsync()
	{
		var instance = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		instance.BeginInitialLoad(progress => SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress), _ => { });
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

	[Fact]
	public void WhenAWatcherIsReplaced_ThenTheNewOneStartsBeforeTheOldOneIsDisposed()
	{
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		var events = new List<string>();
		subject.AttachWatcher(() => new RecordingWatcher("old", events));

		subject.AttachWatcher(() =>
		{
			events.Add("new started");
			return new RecordingWatcher("new", events);
		});

		Assert.Equal(["new started", "old disposed"], events);
	}

	[Fact]
	public void WhenAReplacementWatcherFailsToStart_ThenTheOldOneIsKept()
	{
		using var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		var events = new List<string>();
		var old = new RecordingWatcher("old", events);
		old.Diagnostics.Record("kept", "/watched");
		subject.AttachWatcher(() => old);

		Assert.Throws<IOException>(() => subject.AttachWatcher(() => throw new IOException("start failed")));

		Assert.Empty(events);
		Assert.Single(subject.WatchDiagnostics.Messages);
	}

	[Fact]
	public void WhenTheInstanceIsDisposedWhileAWatcherStarts_ThenThatWatcherIsDisposedNotAttached()
	{
		var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		var events = new List<string>();
		var late = new RecordingWatcher("late", events);
		late.Diagnostics.Record("late", "/watched");

		subject.AttachWatcher(() =>
		{
			subject.Dispose();
			return late;
		});

		Assert.Equal(["late disposed"], events);
		Assert.Empty(subject.WatchDiagnostics.Messages);
	}

	private sealed class RecordingWatcher(string name, List<string> events) : IInstanceWatcher
	{
		public WatchDiagnostics Diagnostics { get; } = new();
		public void Dispose() => events.Add($"{name} disposed");
	}
}
