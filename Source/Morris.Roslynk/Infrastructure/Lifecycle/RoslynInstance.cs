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
	/// The live solution snapshot. Starts as the loaded solution and is swapped after each applied
	/// change, so reads always see the latest in-memory state.
	/// </summary>
	public Solution CurrentSolution => Volatile.Read(ref CurrentSolutionField);

	public void AdvanceTo(Solution solution) =>
		Volatile.Write(ref CurrentSolutionField, solution ?? throw new ArgumentNullException(nameof(solution)));

	public void Dispose()
	{
		Workspace.Dispose();
		WriteLock.Dispose();
	}
}
