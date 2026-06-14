namespace Morris.Roslynk.Infrastructure.Common;

/// <summary>
/// The operational axis, separate from <see cref="ResultStatus"/>: a call may be well-formed
/// yet not currently serviceable because the write base is stale or the solution is still loading.
/// </summary>
public enum OperationalStatus
{
	Ok,
	StaleSnapshot,
	NotReady
}
