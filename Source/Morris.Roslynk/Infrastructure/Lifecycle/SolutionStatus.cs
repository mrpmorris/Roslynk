namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The load state of a solution's in-memory model. <see cref="Building"/> while the initial load or a
/// rebuild is in flight — reads may see the previous snapshot, or none at all before the first load;
/// <see cref="Ready"/> once a current snapshot is published; <see cref="Faulted"/> if the load failed.
/// </summary>
public enum SolutionStatus
{
	Building,
	Ready,
	Faulted
}
