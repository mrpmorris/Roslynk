using System.Diagnostics.Metrics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Tests.Infrastructure.Observability.SolutionMetricsTests;

public class SolutionMetricsTests
{
	[Fact]
	public async Task WhenASolutionIsOpen_ThenItIsReportedAsOneTaggedWithItsPath()
	{
		using var meter = new Meter("test-" + Guid.NewGuid().ToString("N"));
		using var registry = new InstanceRegistry();
		var subject = new SolutionMetrics(meter, registry);
		List<(int Value, string? Path)> measurements = Collect(meter, out MeterListener listener);
		using (listener)
		{
			await registry.GetOrAddAsync(TestSolutions.Simple);
			listener.RecordObservableInstruments();
		}

		(int Value, string? Path) reported = Assert.Single(measurements);
		Assert.Equal(1, reported.Value);
		Assert.Equal(registry.OpenSolutionPaths.Single(), reported.Path);
		Assert.Contains("SimpleSolution", reported.Path);
	}

	[Fact]
	public async Task WhenAllSolutionsAreClosed_ThenNothingIsReported()
	{
		using var meter = new Meter("test-" + Guid.NewGuid().ToString("N"));
		using var registry = new InstanceRegistry();
		var subject = new SolutionMetrics(meter, registry);
		List<(int Value, string? Path)> measurements = Collect(meter, out MeterListener listener);
		using (listener)
		{
			await registry.GetOrAddAsync(TestSolutions.Simple);
			registry.TryClose(TestSolutions.Simple);
			listener.RecordObservableInstruments();
		}

		Assert.Empty(measurements);
	}

	[Fact]
	public void WhenConstructedWithoutAMeter_ThenItThrows()
	{
		using var registry = new InstanceRegistry();

		Assert.Throws<ArgumentNullException>(() => new SolutionMetrics(null!, registry));
	}

	[Fact]
	public void WhenConstructedWithoutARegistry_ThenItThrows()
	{
		using var meter = new Meter("test-" + Guid.NewGuid().ToString("N"));

		Assert.Throws<ArgumentNullException>(() => new SolutionMetrics(meter, null!));
	}

	private static List<(int Value, string? Path)> Collect(Meter meter, out MeterListener listener)
	{
		var measurements = new List<(int Value, string? Path)>();
		listener = new MeterListener();
		listener.InstrumentPublished = (instrument, activeListener) =>
		{
			if (instrument.Meter == meter && instrument.Name == SolutionMetrics.OpenSolutionsName)
				activeListener.EnableMeasurementEvents(instrument);
		};
		listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
		{
			string? path = null;
			foreach (KeyValuePair<string, object?> tag in tags)
			{
				if (tag.Key == SolutionMetrics.SolutionPathTag)
					path = tag.Value as string;
			}

			measurements.Add((measurement, path));
		});
		listener.Start();
		return measurements;
	}
}
