using Microsoft.Extensions.Configuration;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class RoslynkSettingsTests
{
	private static RoslynkSettings Bind(params (string Key, string Value)[] values)
	{
		Dictionary<string, string?> collection = new();
		foreach ((string key, string value) in values)
			collection[$"Roslynk:{key}"] = value;

		return RoslynkSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(collection).Build());
	}

	[Fact]
	public void WhenNothingIsConfigured_ThenEveryValueIsNull()
	{
		RoslynkSettings settings = RoslynkSettings.FromConfiguration(new ConfigurationBuilder().Build());

		Assert.Null(settings.MaxResponseChars);
		Assert.Null(settings.LoadWaitSeconds);
		Assert.Null(settings.IdleEvictionMinutes);
		Assert.Null(settings.EvictionCheckMinutes);
		Assert.Null(settings.Port);
	}

	[Fact]
	public void WhenMaxResponseCharsIsAbsent_ThenTheDefaultBudgetApplies()
	{
		Assert.Equal(80_000, Bind().EffectiveMaxChars);
		Assert.Equal(ResponseBudget.DefaultMaxChars, Bind().EffectiveMaxChars);
	}

	[Fact]
	public void WhenMaxResponseCharsIsZero_ThenItIsRaisedToTheMinimum()
	{
		Assert.Equal(8_000, Bind(("MaxResponseChars", "0")).EffectiveMaxChars);
	}

	[Fact]
	public void WhenMaxResponseCharsIsBelowTheMinimum_ThenItIsRaisedToTheMinimum()
	{
		Assert.Equal(8_000, Bind(("MaxResponseChars", "500")).EffectiveMaxChars);
	}

	[Fact]
	public void WhenMaxResponseCharsIsAboveTheMinimum_ThenItIsUsed()
	{
		Assert.Equal(200_000, Bind(("MaxResponseChars", "200000")).EffectiveMaxChars);
	}

	[Fact]
	public void WhenLoadWaitSecondsIsAbsent_ThenThirtySecondsApplies()
	{
		Assert.Equal(TimeSpan.FromSeconds(30), Bind().EffectiveLoadWaitSeconds);
		Assert.Equal(LoadWaitTimeout.Default.Duration, Bind().EffectiveLoadWaitTimeout.Duration);
	}

	[Fact]
	public void WhenLoadWaitSecondsIsZero_ThenNoWaitApplies()
	{
		Assert.Equal(TimeSpan.Zero, Bind(("LoadWaitSeconds", "0")).EffectiveLoadWaitSeconds);
		Assert.Equal(TimeSpan.Zero, Bind(("LoadWaitSeconds", "0")).EffectiveLoadWaitTimeout.Duration);
	}

	[Fact]
	public void WhenLoadWaitSecondsIsNegative_ThenItClampsToZero()
	{
		Assert.Equal(TimeSpan.Zero, Bind(("LoadWaitSeconds", "-5")).EffectiveLoadWaitSeconds);
	}

	[Fact]
	public void WhenLoadWaitSecondsIsConfigured_ThenItIsUsed()
	{
		Assert.Equal(TimeSpan.FromSeconds(12.5), Bind(("LoadWaitSeconds", "12.5")).EffectiveLoadWaitSeconds);
	}

	[Fact]
	public void WhenIdleEvictionMinutesIsAbsent_ThenThirtyMinutesApplies()
	{
		Assert.Equal(TimeSpan.FromMinutes(30), Bind().EffectiveIdleEvictionTimeout);
	}

	[Fact]
	public void WhenIdleEvictionMinutesIsZero_ThenEvictionIsDisabled()
	{
		Assert.True(Bind(("IdleEvictionMinutes", "0")).EffectiveIdleEvictionTimeout <= TimeSpan.Zero);
	}

	[Fact]
	public void WhenEvictionCheckMinutesIsAbsent_ThenFiveMinutesApplies()
	{
		Assert.Equal(TimeSpan.FromMinutes(5), Bind().EffectiveEvictionCheckInterval);
	}

	[Fact]
	public void WhenEvictionCheckMinutesIsBelowTheFloor_ThenOneMinuteApplies()
	{
		Assert.Equal(TimeSpan.FromMinutes(1), Bind(("EvictionCheckMinutes", "0")).EffectiveEvictionCheckInterval);
	}

	[Fact]
	public void WhenPortIsAbsent_ThenTheDefaultPortApplies()
	{
		Assert.Equal(6502, Bind().EffectivePort);
		Assert.Equal(LoopbackOnlyExtensions.DefaultPort, Bind().EffectivePort);
	}

	[Fact]
	public void WhenPortIsConfigured_ThenItIsUsed()
	{
		Assert.Equal(7000, Bind(("Port", "7000")).EffectivePort);
	}
}
