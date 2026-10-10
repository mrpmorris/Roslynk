using Microsoft.Extensions.Configuration;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class ResponseBudgetConfigurationTests
{
	[Fact]
	public void WhenNoValueIsConfigured_ThenTheDefaultBudgetApplies()
	{
		IConfiguration configuration = new ConfigurationBuilder().Build();

		Assert.Equal(ResponseBudget.DefaultMaxChars, ResponseBudgetConfiguration.FromConfiguration(configuration).MaxChars);
	}

	[Fact]
	public void WhenAValueIsConfigured_ThenItIsUsed()
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ResponseBudgetConfiguration.MaxResponseCharsKey] = "12345",
			})
			.Build();

		Assert.Equal(12_345, ResponseBudgetConfiguration.FromConfiguration(configuration).MaxChars);
	}

	[Fact]
	public void WhenTheValueIsBelowTheMinimum_ThenItIsClampedUp()
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				[ResponseBudgetConfiguration.MaxResponseCharsKey] = "10",
			})
			.Build();

		Assert.Equal(ResponseBudget.MinimumMaxChars, ResponseBudgetConfiguration.FromConfiguration(configuration).MaxChars);
	}
}
