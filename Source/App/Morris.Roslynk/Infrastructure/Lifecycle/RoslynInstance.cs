using System.Threading.Channels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// One loaded solution shared by everyone using it. The current (solution, status) pair lives in an
/// atomically-swapped <see cref="SolutionModel"/>. All mutations (applied edits and watcher folds) and the
/// deferred diagnostics build are ordered work items on a single-consumer channel, so writes never overlap
/// and a build queued after pending writes drains them first. A <see cref="SemaphoreSlimReadWriteLock"/>
/// makes reads wait for an in-flight write to publish its new model; reads otherwise run lock-free on the
/// immutable snapshot. Compilation is forced only by the diagnostics build, gated by <see cref="BuildNeeded"/>
/// with a cached result so repeat calls with no intervening write are free.
/// </summary>
public sealed class RoslynInstance : IDisposable
{
	private SolutionModel CurrentModelField;
	private SolutionWorkspace? WorkspaceField;
	private ProjectLoadTracker LoadTrackerField = new();
	private volatile bool DirtyField;
	private volatile bool BuildNeededField = true;
	private DiagnosticsCacheEntry? DiagnosticsCacheField;
	private long LastAccessedTicks;
	private IDisposable? Watcher;
	private readonly TaskCompletionSource ReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private readonly SemaphoreSlimReadWriteLock Lock = new();
	private readonly Channel<WorkItem> Work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly CancellationTokenSource Shutdown = new();
	private readonly Task Consumer;

	private readonly object RebuildGate = new();
	private Task RebuildInFlight = Task.CompletedTask;

	public SolutionKey Key { get; }

	public RoslynInstance(SolutionKey key)
	{
		Key = key;
		CurrentModelField = SolutionModel.Loading(solution: null);
		LastAccessedTicks = DateTime.UtcNow.Ticks;
		Consumer = Task.Run(ConsumeAsync);
	}

	/// <summary>The current snapshot and its load status, swapped atomically as loads and edits complete.</summary>
	public SolutionModel CurrentModel => Volatile.Read(ref CurrentModelField);

	/// <summary>The workspace the current snapshot was loaded from, or null before the first load completes.</summary>
	public SolutionWorkspace? Workspace => Volatile.Read(ref WorkspaceField);

	/// <summary>
	/// The number of projects loaded so far in the current (or most recent) load; a live count that climbs
	/// while the model is <see cref="SolutionStatus.Building"/>, letting the status tool report progress.
	/// </summary>
	public int LoadedProjects => Volatile.Read(ref LoadTrackerField).Count;

	/// <summary>
	/// The live solution snapshot. Throws if read before the first load completes; tools should read
	/// <see cref="CurrentModel"/> and surface <see cref="SolutionStatus.Building"/> instead of assuming one.
	/// </summary>
	public Solution CurrentSolution =>
		CurrentModel.Solution ?? throw new InvalidOperationException("The solution is still loading.");

	/// <summary>When the instance was last handed out, used by the registry to idle-evict unused solutions.</summary>
	public DateTime LastAccessedUtc => new(Interlocked.Read(ref LastAccessedTicks), DateTimeKind.Utc);

	/// <summary>Records that the instance was just used.</summary>
	public void Touch() => Interlocked.Exchange(ref LastAccessedTicks, DateTime.UtcNow.Ticks);

	/// <summary>
	/// Set by the file watcher when a project / props / sln file changed on disk, which the immutable
	/// snapshot cannot absorb incrementally. The instance rebuilds in the background on its next use.
	/// </summary>
	public bool IsDirty => DirtyField;

	public void MarkDirty() => DirtyField = true;

	/// <summary>Whether the snapshot changed since the last diagnostics build, so the next build must recompile.</summary>
	public bool BuildNeeded => BuildNeededField;

	/// <summary>Hands the instance the watcher that keeps it fresh, disposing any previous one (a rebuild
	/// re-attaches), so the two share a lifetime.</summary>
	public void AttachWatcher(IDisposable watcher)
	{
		Watcher?.Dispose();
		Watcher = watcher;
	}

	/// <summary>A task that completes when the first load finishes, whether it became Ready or Faulted.</summary>
	public Task WaitUntilReadyAsync() => ReadySignal.Task;

	/// <summary>
	/// Acquires the read side of the lock briefly, captures the current immutable model, and releases. A read
	/// that starts while a write is applying blocks here until the write publishes its new model; the caller
	/// then works on the captured immutable snapshot lock-free.
	/// </summary>
	public async Task<SolutionModel> ReadModelAsync(CancellationToken cancellationToken = default)
	{
		using (await Lock.AcquireReadAsync(cancellationToken))
			return CurrentModel;
	}

	/// <summary>
	/// Enqueues a write. The single consumer takes the write side of the lock (so reads wait), publishes
	/// <see cref="SolutionStatus.Updating"/>, runs <paramref name="transform"/> against the latest snapshot,
	/// publishes the edited snapshot as Ready, marks a build needed, and completes with the changed paths.
	/// </summary>
	public Task<IReadOnlyList<string>> EnqueueWriteAsync(Func<Solution, CancellationToken, Task<WriteResult>> transform, CancellationToken cancellationToken = default)
	{
		if (transform is null)
			throw new ArgumentNullException(nameof(transform));

		var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = new WorkItem(() => RunWriteAsync(transform, completion, cancellationToken), exception => completion.TrySetException(exception));
		if (!Work.Writer.TryWrite(item))
			completion.TrySetException(new ObjectDisposedException(nameof(RoslynInstance)));

		return completion.Task;
	}

	/// <summary>
	/// Enqueues a write and automatically triggers background diagnostics computation after the write completes.
	/// This provides Visual Studio-like behavior where editing a file immediately updates diagnostics in the background.
	/// </summary>
	public async Task<IReadOnlyList<string>> EnqueueWriteWithAutoDiagnosticsAsync(Func<Solution, CancellationToken, Task<WriteResult>> transform, Func<Solution, CancellationToken, Task<IReadOnlyList<Diagnostic>>> diagnosticsCompute, CancellationToken cancellationToken = default)
	{
		if (transform is null)
			throw new ArgumentNullException(nameof(transform));
		if (diagnosticsCompute is null)
			throw new ArgumentNullException(nameof(diagnosticsCompute));

		IReadOnlyList<string> changedPaths = await EnqueueWriteAsync(transform, cancellationToken);

		_ = Task.Run(async () =>
		{
			try
			{
				await RequestDiagnosticsAsync("auto", diagnosticsCompute, cancellationToken);
			}
			catch
			{
				// Background diagnostics failed silently diagnostics will be recomputed on next explicit request
			}
		});

		return changedPaths;
	}

	/// <summary>
	/// Enqueues a diagnostics build. Ordered after pending writes (so they drain first); returns a cached
	/// result when nothing changed since the last build with the same <paramref name="cacheKey"/>. The compile
	/// runs without holding the read side, so other reads continue on the uncompiled snapshot meanwhile.
	/// </summary>
	public Task<DiagnosticsResult> RequestDiagnosticsAsync(string cacheKey, Func<Solution, CancellationToken, Task<IReadOnlyList<Diagnostic>>> compute, CancellationToken cancellationToken = default)
	{
		if (cacheKey is null)
			throw new ArgumentNullException(nameof(cacheKey));
		if (compute is null)
			throw new ArgumentNullException(nameof(compute));

		var completion = new TaskCompletionSource<DiagnosticsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = new WorkItem(() => RunDiagnosticsAsync(cacheKey, compute, completion, cancellationToken), exception => completion.TrySetException(exception));
		if (!Work.Writer.TryWrite(item))
			completion.TrySetException(new ObjectDisposedException(nameof(RoslynInstance)));

		return completion.Task;
	}

	private async Task ConsumeAsync()
	{
		try
		{
			await foreach (WorkItem item in Work.Reader.ReadAllAsync(Shutdown.Token))
			{
				try
				{
					await item.Run();
				}
				catch (Exception exception)
				{
					// Backstop: a handler should complete its own TCS, but never let one escape and kill the consumer.
					item.Fault(exception);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			while (Work.Reader.TryRead(out WorkItem? pending))
				pending.Fault(new ObjectDisposedException(nameof(RoslynInstance)));
		}
	}

	private async Task RunWriteAsync(Func<Solution, CancellationToken, Task<WriteResult>> transform, TaskCompletionSource<IReadOnlyList<string>> completion, CancellationToken cancellationToken)
	{
		SolutionModel model = CurrentModel;
		if (model.Solution is null)
		{
			completion.TrySetException(new InvalidOperationException("The solution is still loading."));
			return;
		}

		Solution current = model.Solution;
		using (await Lock.AcquireWriteAsync(cancellationToken))
		{
			Swap(SolutionModel.Updating(current));
			try
			{
				WriteResult result = await transform(current, cancellationToken);
				AdvanceTo(result.Updated);
				BuildNeededField = true;
				Volatile.Write(ref DiagnosticsCacheField, null);
				completion.TrySetResult(result.ChangedPaths);
			}
			catch (Exception exception)
			{
				Swap(SolutionModel.Ready(current));
				completion.TrySetException(exception);
			}
		}
	}

	private async Task RunDiagnosticsAsync(string cacheKey, Func<Solution, CancellationToken, Task<IReadOnlyList<Diagnostic>>> compute, TaskCompletionSource<DiagnosticsResult> completion, CancellationToken cancellationToken)
	{
		SolutionModel model = CurrentModel;
		if (model.Solution is null)
		{
			completion.TrySetException(new InvalidOperationException("The solution is still loading."));
			return;
		}

		Solution solution = model.Solution;
		DiagnosticsCacheEntry? cached = Volatile.Read(ref DiagnosticsCacheField);
		if (!BuildNeededField && cached is not null && cached.Key == cacheKey)
		{
			completion.TrySetResult(new DiagnosticsResult(cached.Diagnostics, cached.Solution));
			return;
		}

		Swap(SolutionModel.Loading(solution));
		try
		{
			IReadOnlyList<Diagnostic> diagnostics = await compute(solution, cancellationToken);
			Swap(SolutionModel.Ready(solution));
			BuildNeededField = false;
			Volatile.Write(ref DiagnosticsCacheField, new DiagnosticsCacheEntry(cacheKey, diagnostics, solution));
			completion.TrySetResult(new DiagnosticsResult(diagnostics, solution));
		}
		catch (Exception exception)
		{
			Swap(SolutionModel.Ready(solution));
			completion.TrySetException(exception);
		}
	}

	/// <summary>
	/// Starts the initial background load. The model stays <see cref="SolutionStatus.Building"/> with no
	/// snapshot until the workspace loads, then flips to <see cref="SolutionStatus.Ready"/>; a load failure
	/// flips it to <see cref="SolutionStatus.Faulted"/>. <paramref name="onReady"/> runs once the snapshot
	/// is available (for example, to attach the file watcher). Call once per instance.
	/// </summary>
	public void BeginInitialLoad(Func<IProgress<ProjectLoadProgress>, Task<SolutionWorkspace>> loader, Action<RoslynInstance> onReady)
	{
		if (loader is null)
			throw new ArgumentNullException(nameof(loader));
		if (onReady is null)
			throw new ArgumentNullException(nameof(onReady));

		var tracker = new ProjectLoadTracker();
		Volatile.Write(ref LoadTrackerField, tracker);

		_ = Task.Run(async () =>
		{
			try
			{
				SolutionWorkspace workspace = await loader(tracker);
				Volatile.Write(ref WorkspaceField, workspace);
				BuildNeededField = true;
				Volatile.Write(ref DiagnosticsCacheField, null);
				Swap(SolutionModel.Ready(workspace.Solution, workspace.ProjectModels));
				onReady(this);
			}
			catch (Exception exception)
			{
				Swap(SolutionModel.Faulted(exception.Message));
			}
			finally
			{
				ReadySignal.TrySetResult();
			}
		});
	}

	/// <summary>
	/// Rebuilds the solution in the background after a build-file or additional-document change. The current
	/// snapshot keeps being served but is marked <see cref="SolutionStatus.Building"/> so readers know it may
	/// be stale; when the fresh workspace loads it is swapped in as <see cref="SolutionStatus.Ready"/> and the
	/// old one is disposed. A failed rebuild flips the model to <see cref="SolutionStatus.Faulted"/>. Does not
	/// wait; callers that need the rebuilt snapshot in a single read use <see cref="EnsureRebuiltAsync"/>.
	/// </summary>
	public void BeginRebuild(Func<IProgress<ProjectLoadProgress>, Task<SolutionWorkspace>> loader, Action<RoslynInstance> onReady)
	{
		if (loader is null)
			throw new ArgumentNullException(nameof(loader));
		if (onReady is null)
			throw new ArgumentNullException(nameof(onReady));

		lock (RebuildGate)
		{
			if (CurrentModel.Solution is not null)
				_ = StartRebuildLocked(loader, onReady);
		}
	}

	/// <summary>
	/// Ensures the snapshot reflects the latest on-disk state and completes only once it is
	/// <see cref="SolutionStatus.Ready"/> (or <see cref="SolutionStatus.Faulted"/>): if the instance is dirty
	/// a rebuild is started; if a rebuild (started here or by a prior non-blocking <see cref="BeginRebuild"/>)
	/// is already in flight it is joined; otherwise it completes immediately. Never blocks on the initial load
	/// (while no snapshot exists yet), so a first read still returns <see cref="SolutionStatus.Building"/>.
	/// Safe for many concurrent readers: at most one rebuild runs and all awaiters share it.
	/// </summary>
	public Task EnsureRebuiltAsync(Func<IProgress<ProjectLoadProgress>, Task<SolutionWorkspace>> loader, Action<RoslynInstance> onReady)
	{
		if (loader is null)
			throw new ArgumentNullException(nameof(loader));
		if (onReady is null)
			throw new ArgumentNullException(nameof(onReady));

		lock (RebuildGate)
		{
			if (DirtyField && CurrentModel.Solution is not null)
				return StartRebuildLocked(loader, onReady);

			return RebuildInFlight;
		}
	}

	private Task StartRebuildLocked(Func<IProgress<ProjectLoadProgress>, Task<SolutionWorkspace>> loader, Action<RoslynInstance> onReady)
	{
		if (!RebuildInFlight.IsCompleted)
			return RebuildInFlight; // A rebuild is already running; share it rather than starting a second.

		DirtyField = false;
		BuildNeededField = true;
		Volatile.Write(ref DiagnosticsCacheField, null);
		var tracker = new ProjectLoadTracker();
		Volatile.Write(ref LoadTrackerField, tracker);
		Swap(SolutionModel.Loading(CurrentModel.Solution));

		RebuildInFlight = Task.Run(async () =>
		{
			try
			{
				SolutionWorkspace workspace = await loader(tracker);
				await PublishRebuildAsync(workspace, onReady);
			}
			catch (Exception exception)
			{
				try { await PublishFaultAsync(exception.Message); }
				catch { /* The instance is shutting down; there is nothing left to publish to. */ }
			}
		});

		return RebuildInFlight;
	}

	/// <summary>
	/// Publishes a freshly rebuilt workspace as the new <see cref="SolutionStatus.Ready"/> snapshot, ordered on
	/// the single-consumer write channel so any queued incremental folds drain and apply first and the
	/// replacement is serialized with them rather than racing their swap (a fold that captured the pre-rebuild
	/// solution could otherwise clobber the rebuild). The swap runs under the write lock, so a reader sees either
	/// the old or the rebuilt snapshot, never a torn one; a fold enqueued after this re-reads disk against the
	/// fresh snapshot.
	/// </summary>
	private Task PublishRebuildAsync(SolutionWorkspace workspace, Action<RoslynInstance> onReady)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = new WorkItem(
			async () =>
			{
				using (await Lock.AcquireWriteAsync(Shutdown.Token))
				{
					SolutionWorkspace? previous = Volatile.Read(ref WorkspaceField);
					Volatile.Write(ref WorkspaceField, workspace);
					Swap(SolutionModel.Ready(workspace.Solution, workspace.ProjectModels));
					BuildNeededField = true;
					Volatile.Write(ref DiagnosticsCacheField, null);
					onReady(this);
					previous?.Dispose();
				}
				completion.TrySetResult();
			},
			exception => completion.TrySetException(exception));

		if (!Work.Writer.TryWrite(item))
		{
			workspace.Dispose(); // The instance is disposed; the rebuilt workspace will never be installed.
			completion.TrySetResult();
		}

		return completion.Task;
	}

	/// <summary>Publishes a rebuild failure as a <see cref="SolutionStatus.Faulted"/> snapshot, ordered on the
	/// write channel like a successful publish so a draining fold cannot mask the fault.</summary>
	private Task PublishFaultAsync(string message)
	{
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var item = new WorkItem(
			async () =>
			{
				using (await Lock.AcquireWriteAsync(Shutdown.Token))
					Swap(SolutionModel.Faulted(message));
				completion.TrySetResult();
			},
			exception => completion.TrySetException(exception));

		if (!Work.Writer.TryWrite(item))
			completion.TrySetResult();

		return completion.Task;
	}

	/// <summary>
	/// Publishes an incrementally-edited snapshot (an applied change or a folded-in source edit) as the new
	/// <see cref="SolutionStatus.Ready"/> model.
	/// </summary>
	public void AdvanceTo(Solution solution)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		Swap(SolutionModel.Ready(solution));
	}

	private void Swap(SolutionModel model) => Volatile.Write(ref CurrentModelField, model);

	public void Dispose()
	{
		Work.Writer.TryComplete();
		Shutdown.Cancel();
		Watcher?.Dispose();
		Volatile.Read(ref WorkspaceField)?.Dispose();
		Lock.Dispose();
		Shutdown.Dispose();
	}

	private sealed class WorkItem
	{
		public Func<Task> Run { get; }
		public Action<Exception> Fault { get; }

		public WorkItem(Func<Task> run, Action<Exception> fault)
		{
			Run = run;
			Fault = fault;
		}
	}

	private sealed record DiagnosticsCacheEntry(string Key, IReadOnlyList<Diagnostic> Diagnostics, Solution Solution);
}