namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

/// <summary>The solutions currently loaded by the server.</summary>
public sealed class GetSolutionStatusResponse
{
	public IReadOnlyList<LoadedSolutionStatus> Solutions { get; }

	public GetSolutionStatusResponse(IReadOnlyList<LoadedSolutionStatus> solutions)
	{
		Solutions = solutions;
	}
}
