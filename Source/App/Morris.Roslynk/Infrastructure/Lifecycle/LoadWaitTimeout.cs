namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// How long a solution-scoped tool waits for an in-flight initial load before it reports
/// <c>error=Indexing</c>. The host reads 'Roslynk:LoadWaitSeconds' and registers the plain value, so the
/// engine stays free of configuration packages; tests construct shorter values directly.
/// </summary>
public sealed record LoadWaitTimeout(TimeSpan Duration)
{
	public static readonly LoadWaitTimeout Default = new(TimeSpan.FromSeconds(30));
}
