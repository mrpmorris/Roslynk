using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Periodically closes solutions that have not been used for a while, so a long-running service does not
/// hold every solution ever opened in memory. The idle window and check interval come from the startup
/// <see cref="RoslynkSettings"/>; an idle window of zero or less disables eviction. The eviction decision
/// itself lives in the registry.
/// </summary>
public sealed class IdleEvictionService : BackgroundService
{
	private readonly InstanceRegistry InstanceRegistry;
	private readonly TimeSpan IdleTimeout;
	private readonly TimeSpan CheckInterval;

	public IdleEvictionService(InstanceRegistry instanceRegistry, RoslynkSettings settings)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ArgumentNullException.ThrowIfNull(settings);
		IdleTimeout = settings.EffectiveIdleEvictionTimeout;
		CheckInterval = settings.EffectiveEvictionCheckInterval;
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
