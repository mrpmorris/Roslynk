namespace Morris.Roslynk.Plugins;

/// <summary>
/// The state of a solution's model. <see cref="Building"/> while it loads or reloads, <see cref="Updating"/>
/// while an edit is being applied (both may still carry the previous snapshot), <see cref="Ready"/> once a
/// current snapshot is published, <see cref="Faulted"/> if the load failed or the solution file is missing.
/// </summary>
public enum SolutionStatus
{
	Building,
	Updating,
	Ready,
	Faulted
}
