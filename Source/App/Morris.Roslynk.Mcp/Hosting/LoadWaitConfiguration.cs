using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Reads how long solution-scoped tools wait for an in-flight initial load from host configuration
/// ('Roslynk:LoadWaitSeconds', or the 'Roslynk__LoadWaitSeconds' environment variable through the same
/// provider 'Roslynk:Port' uses) and hands the engine the plain value, so the core project stays free of
/// configuration packages. The daemon reads the value once at startup: changing it requires a daemon restart.
/// </summary>
public static class LoadWaitConfiguration
{
	public const string LoadWaitSecondsKey = "Roslynk:LoadWaitSeconds";

	public static LoadWaitTimeout FromConfiguration(IConfiguration configuration)
	{
		double seconds = configuration.GetValue(LoadWaitSecondsKey, LoadWaitTimeout.Default.Duration.TotalSeconds);
		return new LoadWaitTimeout(TimeSpan.FromSeconds(Math.Max(0, seconds)));
	}
}
