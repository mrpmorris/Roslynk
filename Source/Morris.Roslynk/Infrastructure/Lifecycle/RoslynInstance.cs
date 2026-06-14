using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// One loaded solution shared by everyone using it: the <see cref="SolutionWorkspace"/> it was loaded
/// from, the live <see cref="CurrentSolution"/> snapshot (swapped as changes are applied), and the
/// single-writer lock that serializes those applies. Session ref-counting and eviction layer on later.
/// </summary>
public sealed class RoslynInstance : IDisposable
{
	private Solution CurrentSolutionField;
	private volatile bool DirtyField;
	private IDisposable? Watcher;

	public SolutionKey Key { get; }
	public SolutionWorkspace Workspace { get; }

	/// <summary>Serializes writes to this instance so two applies cannot interleave.</summary>
	public SemaphoreSlim WriteLock { get; } = new(initialCount: 1, maxCount: 1);

	public RoslynInstance(SolutionKey key, SolutionWorkspace workspace)
	{
		Key = key;
		Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		CurrentSolutionField = workspace.Solution;
	}

	/// <summary>
	/// Set by the file watcher when a project / props / sln file changed on disk, which the immutable
	/// snapshot cannot absorb incrementally. The registry reloads the instance on its next use.
	/// </summary>
	public bool IsDirty => DirtyField;

	public void MarkDirty() => DirtyField = true;

	/// <summary>Hands the instance the watcher that keeps it fresh, so the two share a lifetime.</summary>
	public void AttachWatcher(IDisposable watcher) => Watcher = watcher;

	/// <summary>
	/// The live solution snapshot. Starts as the loaded solution and is swapped after each applied
	/// change, so reads always see the latest in-memory state.
	/// </summary>
	public Solution CurrentSolution => Volatile.Read(ref CurrentSolutionField);

	public void AdvanceTo(Solution solution) =>
		Volatile.Write(ref CurrentSolutionField, solution ?? throw new ArgumentNullException(nameof(solution)));

	public void Dispose()
	{
		Watcher?.Dispose();
		Workspace.Dispose();
		WriteLock.Dispose();
	}
}
