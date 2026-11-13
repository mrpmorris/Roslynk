using Microsoft.Extensions.Logging;

namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>Host facilities a plugin uses at initialization and on every tool call.</summary>
public interface IPluginContext
{
	/// <summary>The factory the plugin creates its category loggers from.</summary>
	ILoggerFactory LoggerFactory { get; }

	/// <summary>
	/// The process-wide response budget in characters. A plugin can size its own results with
	/// it before the invocation wrapper's backstop cuts an oversized result.
	/// </summary>
	int MaxResponseChars { get; }

	/// <summary>
	/// Loads the solution if it is not already loaded, waits for the build, then runs
	/// <paramref name="use"/> against the current snapshot. The callback may run more than once
	/// per call; the returned result is a terminal outcome in every case.
	/// </summary>
	/// <param name="solutionId">The absolute solution path in either slash form, or the solutionId echoed by open_solution.</param>
	ValueTask<IUseSolutionResult<T>> UseSolutionAsync<T>(string solutionId, Func<ISolutionContext, CancellationToken, ValueTask<T>> use, CancellationToken cancellationToken = default);
}
