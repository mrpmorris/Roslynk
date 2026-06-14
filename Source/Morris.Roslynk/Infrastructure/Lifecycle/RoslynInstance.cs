using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// One loaded solution shared by everyone using it: the <see cref="SolutionWorkspace"/> plus the
/// identity it is keyed by. The single-writer lock, session set, and eviction are layered on here as
/// later tiers need them.
/// </summary>
public sealed class RoslynInstance : IDisposable
{
	public SolutionKey Key { get; }
	public SolutionWorkspace Workspace { get; }

	public RoslynInstance(SolutionKey key, SolutionWorkspace workspace)
	{
		Key = key;
		Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
	}

	public void Dispose() => Workspace.Dispose();
}
