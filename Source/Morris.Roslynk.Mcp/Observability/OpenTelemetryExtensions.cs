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
/// <para>
/// OpenTelemetry is only wired when an endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>).
/// With no endpoint there is nothing to export to, so nothing is wired and nothing is emitted — the
/// server runs exactly as before.
/// </para>
/// </summary>
public static class OpenTelemetryExtensions
{
	public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

	public static WebApplicationBuilder AddRoslynkObservability(this WebApplicationBuilder builder)
	{
		string? otlpEndpoint = builder.Configuration[OtlpEndpointKey];
		if (string.IsNullOrWhiteSpace(otlpEndpoint))
			return builder;

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
