namespace Morris.Roslynk.Plugins;

/// <summary>
/// Read access to the solutions the daemon holds, the same in-memory models its own tools use. A plugin gets
/// one through <see cref="IRoslynkPlugin.RunAsync"/>, and its MCP tool classes can take one in their constructor.
/// </summary>
public interface ISolutionProvider
{
	/// <summary>
	/// The current model of the solution at <paramref name="solutionId"/> (its path, or the <c>solutionId</c> a
	/// tool returned). As <c>open_solution</c> does, this opens the solution if it is not open, waits up to the
	/// daemon's load timeout, then returns whatever it has: <see cref="SolutionStatus.Building"/> with no model
	/// if the first load has not finished. It counts as use, so a solution a plugin keeps reading is not evicted
	/// as idle. <paramref name="cancellationToken"/> cancels the wait, not the load.
	/// </summary>
	Task<GetModelResponse> GetModelAsync(string solutionId, CancellationToken cancellationToken = default);
}
