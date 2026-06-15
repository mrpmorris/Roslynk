using System.Diagnostics.Metrics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// Publishes the <c>roslynk.solutions.open</c> metric — one measurement of <c>1</c> per solution currently
/// open in the <see cref="InstanceRegistry"/>, tagged with <c>solution.path</c>. Summed it is the number of
/// open solutions; grouped by the tag it shows which solutions are open. It is observed at collection time,
/// so the many open, close, evict and reload paths never have to keep a running total in sync.
/// </summary>
public sealed class SolutionMetrics
{
	public const string OpenSolutionsName = "roslynk.solutions.open";
	public const string SolutionPathTag = "solution.path";

	public SolutionMetrics(Meter meter, InstanceRegistry instanceRegistry)
	{
		if (meter is null)
			throw new ArgumentNullException(nameof(meter));
		if (instanceRegistry is null)
			throw new ArgumentNullException(nameof(instanceRegistry));

		// The meter owns the instrument's lifetime; we only need to register it, not hold it.
		_ = meter.CreateObservableUpDownCounter(
			name: OpenSolutionsName,
			observeValues: () => Observe(instanceRegistry),
			unit: "{solution}",
			description: "Number of solutions currently open, tagged by solution path.");
	}

	private static IEnumerable<Measurement<int>> Observe(InstanceRegistry instanceRegistry)
	{
		foreach (string path in instanceRegistry.OpenSolutionPaths)
			yield return new Measurement<int>(1, new KeyValuePair<string, object?>(SolutionPathTag, path));
	}
}
