using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Owns an <see cref="MSBuildWorkspace"/> and the immutable <see cref="Solution"/> snapshot loaded from
/// a <c>.sln</c> / <c>.slnx</c>. Lifecycle, sessions, and the single-writer lock are layered on top of
/// this by the instance registry; this type is just the load + snapshot.
/// </summary>
public sealed class SolutionWorkspace : IDisposable
{
	private readonly MSBuildWorkspace Workspace;

	public Solution Solution { get; }

	/// <summary>Partial-load failures reported by MSBuild while opening the solution.</summary>
	public IReadOnlyList<string> LoadDiagnostics { get; }

	private SolutionWorkspace(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<string> loadDiagnostics)
	{
		Workspace = workspace;
		Solution = solution;
		LoadDiagnostics = loadDiagnostics;
	}

	public static async Task<SolutionWorkspace> LoadAsync(string solutionPath, CancellationToken cancellationToken = default)
	{
		if (solutionPath is null)
			throw new ArgumentNullException(nameof(solutionPath));

		MsBuildRegistrar.EnsureRegistered();

		var loadDiagnostics = new ConcurrentBag<string>();
		MSBuildWorkspace workspace = MSBuildWorkspace.Create();

		using (workspace.RegisterWorkspaceFailedHandler(e => loadDiagnostics.Add(e.Diagnostic.Message)))
		{
			Solution solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
			return new SolutionWorkspace(workspace, solution, loadDiagnostics.ToArray());
		}
	}

	public void Dispose() => Workspace.Dispose();
}
