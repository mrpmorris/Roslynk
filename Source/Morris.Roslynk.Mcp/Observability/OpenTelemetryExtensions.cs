using Morris.Roslynk.Infrastructure.Observability;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Wires OpenTelemetry traces, metrics, and logs through the OTLP exporter. The exporter owns the
/// connection (batching, retry, reconnect) and reads its endpoint from the standard
/// <c>OTEL_EXPORTER_OTLP_*</c> configuration; Roslynk writes no transport code.
/// </summary>
public static class OpenTelemetryExtensions
{
	public static WebApplicationBuilder AddRoslynkObservability(this WebApplicationBuilder builder)
	{
		builder.Services
			.AddOpenTelemetry()
			.ConfigureResource(resource => resource.AddService(serviceName: "Roslynk"))
			.WithTracing(tracing => tracing
				.AddSource(RoslynkActivitySource.Name)
				.AddAspNetCoreInstrumentation()
				.AddOtlpExporter())
			.WithMetrics(metrics => metrics
				.AddMeter(RoslynkMeter.Name)
				.AddAspNetCoreInstrumentation()
				.AddOtlpExporter());

		builder.Logging.AddOpenTelemetry(logging => logging.AddOtlpExporter());

		return builder;
	}
}
