namespace Morris.Roslynk.Plugins;

/// <summary>
/// A plugin the daemon loads from its plugins folder. The daemon creates each one with its public parameterless
/// constructor, starts <see cref="RunAsync"/> once when the daemon starts, and cancels <c>stopping</c> when it
/// shuts down. An exception from <see cref="RunAsync"/> is logged; it stops neither the daemon nor other plugins.
/// </summary>
public interface IRoslynkPlugin
{
	/// <summary>
	/// The plugin's work for the life of the daemon: return when it has nothing more to do, or when
	/// <paramref name="stopping"/> is cancelled.
	/// </summary>
	Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping);
}
