namespace Morris.Roslynk.Plugins.V1;

/// <summary>The kind of solution lifecycle change an <see cref="ISolutionEvent"/> describes.</summary>
public enum SolutionEventKind
{
	/// <summary>An initial load or rebuild started, or a solution was opened before its first load finished.</summary>
	Building,

	/// <summary>A load, rebuild or incremental update published a usable snapshot.</summary>
	Ready,

	/// <summary>An initial load or rebuild failed.</summary>
	Failed,

	/// <summary>The solution was unloaded (idle eviction).</summary>
	Evicted,
}
