namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The load state of a solution's in-memory model. <see cref="Building"/> while the initial load, a
/// rebuild, or a diagnostics compile is in flight; <see cref="Updating"/> while an edit is being applied;
/// reads may see the previous snapshot, or none at all before the first load; <see cref="Ready"/> once a
/// current snapshot is published; <see cref="Faulted"/> if the load failed. Both <see cref="Building"/> and
/// <see cref="Updating"/> mean "momentarily behind" and are non-fatal.
/// </summary>
public enum SolutionStatus
{
	Building,
	Updating,
	Ready,
	Faulted
}
