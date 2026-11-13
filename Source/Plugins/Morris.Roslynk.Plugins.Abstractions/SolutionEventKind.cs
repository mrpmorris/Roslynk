namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>The kind of solution lifecycle change an <see cref="ISolutionEvent"/> describes.</summary>
public enum SolutionEventKind
{
	/// <summary>An initial load or rebuild is in flight.</summary>
	Building,

	/// <summary>A load, rebuild or incremental update published a usable snapshot.</summary>
	Ready,

	/// <summary>An initial load or rebuild failed.</summary>
	Failed,

	/// <summary>The solution was unloaded (idle eviction).</summary>
	Evicted,
}
