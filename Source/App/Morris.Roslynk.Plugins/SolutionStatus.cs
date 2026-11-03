namespace Morris.Roslynk.Plugins;

/// <summary>
/// The load state of a solution's in-memory model, the same values Roslynk's own tools report. Both
/// <see cref="Building"/> and <see cref="Updating"/> mean "momentarily behind" and are not failures.
/// </summary>
public enum SolutionStatus
{
	/// <summary>The initial load, a rebuild or a diagnostics compile is in flight; before the first load there is no snapshot.</summary>
	Building,

	/// <summary>An edit is being applied; reads see the previous snapshot.</summary>
	Updating,

	/// <summary>A current snapshot is published.</summary>
	Ready,

	/// <summary>The load failed, or the solution could not be read.</summary>
	Faulted
}
