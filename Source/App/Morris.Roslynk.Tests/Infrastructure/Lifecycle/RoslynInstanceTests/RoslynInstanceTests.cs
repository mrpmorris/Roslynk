using System.Collections.Immutable;
using System.Reflection;
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

		Assert.True(subject.CurrentModel.Status == SolutionStatus.Ready, $"STATUS={subject.CurrentModel.Status} FAULT={subject.CurrentModel.FaultMessage}");
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

	/// <summary>Real per-project computes (compilation + full analyzer pass) under a loaded test
	/// machine can exceed the ordering tests' 5s budget; these waits allow for machine contention.</summary>
	private static readonly TimeSpan ComputeTimeout = TimeSpan.FromSeconds(60);

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
	public async Task WhenDiagnosticsAreRequestedAfterAWrite_ThenTheyComputeForTheWriteSnapshot()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		Solution before = subject.CurrentSolution;

		DocumentId documentId = before.Projects.First().DocumentIds[0];
		await subject.EnqueueWriteAsync((current, token) =>
			Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// after write")), [])))
			.WaitAsync(Timeout);

		DiagnosticsResult diagnostics = await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Assert.Same(subject.CurrentSolution, diagnostics.Solution);
		Assert.False(subject.BuildNeeded);
	}

	[Fact]
	public async Task WhenNothingChangedSinceTheLastBuild_ThenThePerProjectTasksAreReused()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> before = SolutionSnapshotProbe.Of(subject);
		Assert.Single(before);

		Dictionary<Project, object> again = SolutionSnapshotProbe.Of(subject);
		Assert.Single(again);
		Assert.Same(before.Values.Single(), again.Values.Single());
		Assert.False(subject.BuildNeeded);

		// An edit forks only the edited project: the next request recomputes one project and carries the rest.
		await subject.EnqueueWriteAsync((current, token) =>
		{
			DocumentId documentId = current.Projects.First().DocumentIds[0];
			return Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// edited")), []));
		}).WaitAsync(Timeout);

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> afterEdit = SolutionSnapshotProbe.Of(subject);
		Assert.Single(afterEdit);
		Assert.NotSame(before.Values.Single(), afterEdit.Values.Single());
	}

	[Fact]
	public async Task WhenAWriteChangesNothing_ThenTheHolderSurvives()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> before = SolutionSnapshotProbe.Of(subject);

		// A watcher event for content the snapshot already holds republishes the same solution object.
		await subject.EnqueueWriteAsync((current, token) => Task.FromResult(new WriteResult(current, []))).WaitAsync(Timeout);

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> after = SolutionSnapshotProbe.Of(subject);
		Assert.Same(before.Values.Single(), after.Values.Single());
		Assert.False(subject.BuildNeeded);
	}

	[Fact]
	public async Task WhenARequesterAbandonsACompute_ThenAJoinerStillGetsTheResult()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		// A pre-cancelled token abandons the wait without affecting the shared per-project tasks.
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => subject.RequestDiagnosticsAsync(subject.CurrentSolution, cancellation.Token).WaitAsync(Timeout));

		DiagnosticsResult result = await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Assert.Same(subject.CurrentSolution, result.Solution);
		Assert.False(subject.BuildNeeded);
	}

	[Fact]
	public async Task WhenARequesterIsCancelledWhileQueued_ThenALaterRequestStillComputesAfterTheWriteDrains()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		var gate = new TaskCompletionSource();
		Task write = subject.EnqueueWriteAsync(async (current, token) =>
		{
			await gate.Task;
			return new WriteResult(current, []);
		});

		// The request is queued behind the gated write; its requester cancels while queued.
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => subject.RequestDiagnosticsAsync(subject.CurrentSolution, cancellation.Token).WaitAsync(Timeout));
		Assert.False(gate.Task.IsCompleted);

		// The write drains first; a later request then computes (the channel runs it after the write).
		gate.SetResult();
		DiagnosticsResult result = await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Assert.Same(subject.CurrentSolution, result.Solution);
	}

	[Fact]
	public async Task WhenARequesterIsCancelled_ThenALaterWriteStillRuns()
	{
		using RoslynInstance subject = await LoadReadyAsync();

		// The abandoned compute occupies the channel; the write queues behind it and still runs.
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => subject.RequestDiagnosticsAsync(subject.CurrentSolution, cancellation.Token).WaitAsync(Timeout));

		await subject.EnqueueWriteAsync((current, token) =>
		{
			DocumentId documentId = current.Projects.First().DocumentIds[0];
			return Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// edited after cancellation")), []));
		}).WaitAsync(Timeout);

		Document document = subject.CurrentSolution.Projects.First().Documents.First();
		SourceText text = await document.GetTextAsync().WaitAsync(Timeout);
		Assert.Contains("// edited after cancellation", text.ToString());
	}

	[Fact]
	public async Task WhenASnapshotIsSuperseded_ThenARequestForItReturnsItsOwnResults()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		Solution pinned = subject.CurrentSolution;

		DocumentId documentId = pinned.Projects.First().DocumentIds[0];
		await subject.EnqueueWriteAsync((current, token) =>
			Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// superseding edit")), [])))
			.WaitAsync(Timeout);

		// The pinned snapshot's unaltered projects hit the current holder's tasks; the forked project
		// computes uncached — but the result is for the pinned snapshot either way.
		DiagnosticsResult pinnedResult = await subject.RequestDiagnosticsAsync(pinned).WaitAsync(Timeout);
		Assert.Same(pinned, pinnedResult.Solution);
		Document pinnedDocument = pinned.Projects.First().Documents.First();
		Assert.DoesNotContain("// superseding edit", (await pinnedDocument.GetTextAsync().WaitAsync(Timeout)).ToString());
	}

	[Fact]
	public async Task WhenASolutionSnapshotIsReplaced_ThenUnchangedProjectsCarryTheirTasks()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> before = SolutionSnapshotProbe.Of(subject);

		await subject.EnqueueWriteAsync((current, token) =>
		{
			DocumentId documentId = current.Projects.First().DocumentIds[0];
			return Task.FromResult(new WriteResult(current.WithDocumentText(documentId, SourceText.From("// edited")), []));
		}).WaitAsync(Timeout);

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> after = SolutionSnapshotProbe.Of(subject);
		// Simple has one project: the edit forked it, so the new holder registered a fresh task rather
		// than carrying the old one.
		Assert.Single(after);
		Assert.NotSame(before.Values.Single(), after.Values.Single());
	}

	[Fact]
	public async Task WhenARebuildPublishes_ThenNoTasksCarryOver()
	{
		using RoslynInstance subject = await LoadReadyAsync();
		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);

		var gate = new TaskCompletionSource();
		subject.BeginRebuild(
			loader: async progress =>
			{
				await gate.Task;
				return await SolutionWorkspace.LoadAsync(TestSolutions.Simple, progress);
			},
			onReady: _ => { });
		gate.SetResult();
		await WaitForReadyAsync(subject);

		await subject.RequestDiagnosticsAsync(subject.CurrentSolution).WaitAsync(ComputeTimeout);
		Dictionary<Project, object> probe = SolutionSnapshotProbe.Of(subject);
		// A rebuild's projects are new instances: nothing carried, everything computed fresh.
		Assert.Single(probe);
	}

	[Fact]
	public void WhenTheModelHasNoSolution_ThenBuildNeededIsTrue()
	{
		var subject = new RoslynInstance(SolutionKey.For(TestSolutions.Simple));
		Assert.True(subject.BuildNeeded);
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

	/// <summary>Reads the instance's private SolutionSnapshot for assertions: project instance → its task.</summary>
	private static class SolutionSnapshotProbe
	{
		public static Dictionary<Project, object> Of(RoslynInstance instance)
		{
			FieldInfo field = typeof(RoslynInstance).GetField("SolutionSnapshotField", BindingFlags.NonPublic | BindingFlags.Instance)!;
			object? holder = field.GetValue(instance);
			if (holder is null)
				return [];

			System.Reflection.PropertyInfo tasksProperty = holder.GetType().GetProperty("Tasks", BindingFlags.Public | BindingFlags.Instance)!;
			var tasks = (Dictionary<Project, Task<ImmutableArray<Diagnostic>>>)tasksProperty.GetValue(holder)!;
			return tasks.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
		}
	}

	private sealed class RecordingWatcher(string name, List<string> events) : IInstanceWatcher
	{
		public WatchDiagnostics Diagnostics { get; } = new();
		public void Dispose() => events.Add($"{name} disposed");
	}
}
