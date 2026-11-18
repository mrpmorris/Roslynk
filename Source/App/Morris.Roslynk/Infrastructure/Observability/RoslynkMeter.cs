using System.Diagnostics.Metrics;

namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// The single <see cref="Meter"/> all Roslynk metrics are emitted from.
/// </summary>
public static class RoslynkMeter
{
	public const string Name = "Morris.Roslynk";

	public static readonly Meter Instance = new(Name);

	public const string LoadDurationName = "roslynk.solutions.load.duration";
	public const string PluginStartupDurationName = "roslynk.plugins.startup.duration";
	public const string SolutionPathTag = "roslynk.solution.path";
	public const string PluginNameTag = "roslynk.plugin.name";
	public const string ResultTag = "roslynk.result";

	private static readonly Histogram<double> LoadDurationHistogram = Instance.CreateHistogram<double>(
		name: LoadDurationName,
		unit: "s",
		description: "Wall-clock time to load a solution, tagged by solution path and result.");

	private static readonly Histogram<double> PluginStartupHistogram = Instance.CreateHistogram<double>(
		name: PluginStartupDurationName,
		unit: "s",
		description: "Wall-clock time to start a plugin (load through subscription), tagged by plugin name and result.");

	public static void RecordLoadDuration(string solutionPath, TimeSpan duration, bool success)
	{
		KeyValuePair<string, object?> pathTag = new(SolutionPathTag, solutionPath);
		KeyValuePair<string, object?> resultTag = new(ResultTag, success ? "ready" : "faulted");
		LoadDurationHistogram.Record(duration.TotalSeconds, pathTag, resultTag);
	}

	public static void RecordPluginStartupDuration(string pluginName, TimeSpan duration, bool success)
	{
		KeyValuePair<string, object?> nameTag = new(PluginNameTag, pluginName);
		KeyValuePair<string, object?> resultTag = new(ResultTag, success ? "loaded" : "failed");
		PluginStartupHistogram.Record(duration.TotalSeconds, nameTag, resultTag);
	}
}
