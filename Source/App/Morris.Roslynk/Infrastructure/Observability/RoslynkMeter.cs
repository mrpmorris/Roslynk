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
	public const string SolutionPathTag = "roslynk.solution.path";
	public const string ResultTag = "roslynk.result";

	private static readonly Histogram<double> LoadDurationHistogram = Instance.CreateHistogram<double>(
		name: LoadDurationName,
		unit: "s",
		description: "Wall-clock time to load a solution, tagged by solution path and result.");

	public static void RecordLoadDuration(string solutionPath, TimeSpan duration, bool success)
	{
		KeyValuePair<string, object?> pathTag = new(SolutionPathTag, ActivityTags.Truncate(solutionPath));
		KeyValuePair<string, object?> resultTag = new(ResultTag, success ? "ready" : "faulted");
		LoadDurationHistogram.Record(duration.TotalSeconds, pathTag, resultTag);
	}
}
