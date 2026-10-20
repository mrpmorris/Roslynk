using Microsoft.Extensions.Configuration;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class LoadWaitConfigurationTests
{
	[Fact]
	public void WhenNoValueIsConfigured_ThenTheDefaultTimeoutApplies()
	{
		IConfiguration configuration = new ConfigurationBuilder().Build();

		Assert.Equal(LoadWaitTimeout.Default.Duration, LoadWaitConfiguration.FromConfiguration(configuration).Duration);
	}

	[Fact]
	public void WhenAValueIsConfigured_ThenItIsUsed()
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[LoadWaitConfiguration.LoadWaitSecondsKey] = "12.5",
			})
			.Build();

		Assert.Equal(TimeSpan.FromSeconds(12.5), LoadWaitConfiguration.FromConfiguration(configuration).Duration);
	}

	[Fact]
	public void WhenTheValueIsNegative_ThenItIsClampedToZero()
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[LoadWaitConfiguration.LoadWaitSecondsKey] = "-5",
			})
			.Build();

		Assert.Equal(TimeSpan.Zero, LoadWaitConfiguration.FromConfiguration(configuration).Duration);
	}
}
