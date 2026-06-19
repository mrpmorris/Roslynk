using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// An immutable snapshot of a loaded solution together with its <see cref="Status"/>. Instances are swapped
/// atomically on <see cref="RoslynInstance"/>, so a reader always sees a consistent (solution, status) pair
/// without locking. <see cref="Solution"/> is null only before the very first load completes.
/// </summary>
public sealed class SolutionModel
{
	public required SolutionStatus Status { get; init; }
	public Solution? Solution { get; init; }
	public string? FaultMessage { get; init; }

	/// <summary>A load or rebuild in flight, optionally still serving the previous <paramref name="solution"/>.</summary>
	public static SolutionModel Loading(Solution? solution) =>
		new() { Status = SolutionStatus.Building, Solution = solution };

	/// <summary>An edit being applied; the previous <paramref name="solution"/> is still served until it completes.</summary>
	public static SolutionModel Updating(Solution solution) =>
		new() { Status = SolutionStatus.Updating, Solution = solution };

	/// <summary>A published snapshot ready to be read.</summary>
	public static SolutionModel Ready(Solution solution) =>
		new() { Status = SolutionStatus.Ready, Solution = solution };

	/// <summary>A load that failed; <paramref name="faultMessage"/> explains why and no snapshot is served.</summary>
	public static SolutionModel Faulted(string faultMessage) =>
		new() { Status = SolutionStatus.Faulted, FaultMessage = faultMessage };
}
