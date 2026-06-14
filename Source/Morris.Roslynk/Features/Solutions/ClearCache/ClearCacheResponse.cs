namespace Morris.Roslynk.Features.Solutions.ClearCache;

/// <summary>How many loaded solutions were closed.</summary>
public sealed class ClearCacheResponse
{
	public int Closed { get; }

	public ClearCacheResponse(int closed)
	{
		Closed = closed;
	}
}
