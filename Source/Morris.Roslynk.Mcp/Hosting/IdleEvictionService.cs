using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Periodically closes solutions that have not been used for a while, so a long-running service does not
/// hold every solution ever opened in memory. The idle window and check interval are configurable; an
/// idle window of zero or less disables eviction. The eviction decision itself lives in the registry.
/// </summary>
public sealed class IdleEvictionService : BackgroundService
{
	private readonly InstanceRegistry InstanceRegistry;
	private readonly TimeSpan IdleTimeout;
	private readonly TimeSpan CheckInterval;

	public IdleEvictionService(InstanceRegistry instanceRegistry, IConfiguration configuration)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		IdleTimeout = TimeSpan.FromMinutes(configuration.GetValue("Roslynk:IdleEvictionMinutes", 30.0));
		CheckInterval = TimeSpan.FromMinutes(Math.Max(1.0, configuration.GetValue("Roslynk:EvictionCheckMinutes", 5.0)));
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (IdleTimeout <= TimeSpan.Zero)
			return; // Eviction disabled.

		using var timer = new PeriodicTimer(CheckInterval);
		while (await timer.WaitForNextTickAsync(stoppingToken))
			InstanceRegistry.EvictIdle(IdleTimeout, DateTime.UtcNow);
	}
}
