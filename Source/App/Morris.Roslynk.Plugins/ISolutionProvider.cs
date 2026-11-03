namespace Morris.Roslynk.Plugins;

/// <summary>
/// Read access to the solutions the daemon holds, the same in-memory models its own tools use. A plugin gets
/// one through <see cref="IRoslynkPlugin.RunAsync"/>, and its MCP tool classes can take one in their constructor.
/// </summary>
public interface ISolutionProvider
{
	/// <summary>
	/// The current model of the solution at <paramref name="solutionId"/> (its path, or the <c>solutionId</c> a
	/// tool returned), read the way Roslynk's own tools read it. The solution is opened if it is not open, and the
	/// call waits up to the daemon's load timeout for its first load, then returns whatever there is:
	/// <see cref="SolutionStatus.Building"/> with no model if that load has not finished. A solution that must be
	/// reloaded from disk (a project or build file changed) is reloaded and awaited in full, which on a large
	/// solution can take as long as its first load. Each call counts as use, so a solution a plugin keeps reading
	/// is not evicted as idle.
	/// </summary>
	/// <param name="solutionId">The solution file's path, or a <c>solutionId</c> a Roslynk tool returned.</param>
	/// <param name="cancellationToken">Cancels the wait, not the load.</param>
	/// <returns>
	/// The model. A missing solution file, a failed load, or a solution closed while it was being read is
	/// <see cref="SolutionStatus.Faulted"/> with the reason in <see cref="GetModelResponse.ErrorMessage"/>,
	/// never an exception.
	/// </returns>
	/// <exception cref="ArgumentException"><paramref name="solutionId"/> is null, empty or white space.</exception>
	/// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
	Task<GetModelResponse> GetModelAsync(string solutionId, CancellationToken cancellationToken = default);
}
