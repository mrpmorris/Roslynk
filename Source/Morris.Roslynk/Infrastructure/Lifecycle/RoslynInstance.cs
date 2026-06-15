using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// One loaded solution shared by everyone using it. The current (solution, status) pair lives in an
/// atomically-swapped <see cref="SolutionModel"/>: the initial load and any rebuild run in the
/// background, flipping the model to <see cref="SolutionStatus.Building"/> while in flight — still
/// serving the previous snapshot during a rebuild — and back to <see cref="SolutionStatus.Ready"/> when
/// done, so reads never block and never see a torn state. The single-writer lock serializes applied
/// edits; the last-access stamp lets the registry idle-evict.
/// </summary>
public sealed class RoslynInstance : IDisposable
{
	private SolutionModel CurrentModelField;
	private SolutionWorkspace? WorkspaceField;
	private ProjectLoadTracker LoadTrackerField = new();
	private volatile bool DirtyField;
	private long LastAccessedTicks;
	private long SnapshotCounter;
	private IDisposable? Watcher;
	private readonly TaskCompletionSource ReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public SolutionKey Key { get; }

	/// <summary>Serializes writes to this instance so two applies cannot interleave.</summary>
	public SemaphoreSlim WriteLock { get; } = new(initialCount: 1, maxCount: 1);

	public RoslynInstance(SolutionKey key)
	{
		Key = key;
		CurrentModelField = SolutionModel.Loading(NextSnapshotId(), solution: null);
		LastAccessedTicks = DateTime.UtcNow.Ticks;
	}

	/// <summary>The current snapshot and its load status, swapped atomically as loads and edits complete.</summary>
	public SolutionModel CurrentModel => Volatile.Read(ref CurrentModelField);

	/// <summary>The workspace the current snapshot was loaded from, or null before the first load completes.</summary>
	public SolutionWorkspace? Workspace => Volatile.Read(ref WorkspaceField);

	/// <summary>
	/// The number of projects loaded so far in the current (or most recent) load — a live count that climbs
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
				Swap(SolutionModel.Ready(NextSnapshotId(), workspace.Solution));
				onReady(this);
			}
			catch (Exception exception)
			{
				Swap(SolutionModel.Faulted(NextSnapshotId(), exception.Message));
			}
			finally
			{
				ReadySignal.TrySetResult();
			}
		});
	}

	/// <summary>
	/// Rebuilds the solution in the background after a build-file change. The current snapshot keeps being
	/// served but is marked <see cref="SolutionStatus.Building"/> so readers know it may be stale; when the
	/// fresh workspace loads it is swapped in as <see cref="SolutionStatus.Ready"/> and the old one is
	/// disposed. A failed rebuild flips the model to <see cref="SolutionStatus.Faulted"/>.
	/// </summary>
	public void BeginRebuild(Func<IProgress<ProjectLoadProgress>, Task<SolutionWorkspace>> loader, Action<RoslynInstance> onReady)
	{
		if (loader is null)
			throw new ArgumentNullException(nameof(loader));
		if (onReady is null)
			throw new ArgumentNullException(nameof(onReady));

		DirtyField = false;
		var tracker = new ProjectLoadTracker();
		Volatile.Write(ref LoadTrackerField, tracker);
		Swap(SolutionModel.Loading(NextSnapshotId(), CurrentModel.Solution));

		_ = Task.Run(async () =>
		{
			try
			{
				SolutionWorkspace workspace = await loader(tracker);
				SolutionWorkspace? previous = Volatile.Read(ref WorkspaceField);
				Volatile.Write(ref WorkspaceField, workspace);
				Swap(SolutionModel.Ready(NextSnapshotId(), workspace.Solution));
				onReady(this);
				previous?.Dispose();
			}
			catch (Exception exception)
			{
				Swap(SolutionModel.Faulted(NextSnapshotId(), exception.Message));
			}
		});
	}

	/// <summary>
	/// Publishes an incrementally-edited snapshot (an applied change or a folded-in source edit) as the new
	/// <see cref="SolutionStatus.Ready"/> model with a fresh <see cref="SolutionModel.SnapshotId"/>.
	/// </summary>
	public void AdvanceTo(Solution solution)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		Swap(SolutionModel.Ready(NextSnapshotId(), solution));
	}

	private void Swap(SolutionModel model) => Volatile.Write(ref CurrentModelField, model);

	private string NextSnapshotId() =>
		Interlocked.Increment(ref SnapshotCounter).ToString(CultureInfo.InvariantCulture);

	public void Dispose()
	{
		Watcher?.Dispose();
		Volatile.Read(ref WorkspaceField)?.Dispose();
		WriteLock.Dispose();
	}
}
