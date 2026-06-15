using Morris.Roslynk.Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Wires OpenTelemetry traces, metrics, and logs through a single OTLP exporter (UseOtlpExporter). The
/// exporter owns the connection (batching, retry, reconnect) and reads its endpoint from the standard
/// <c>OTEL_EXPORTER_OTLP_*</c> configuration; Roslynk writes no transport code.
/// <para>
/// OpenTelemetry is only wired when an endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>).
/// With no endpoint there is nothing to export to, so nothing is wired and nothing is emitted; the
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

		builder.Logging.AddOpenTelemetry(logging =>
		{
			logging.IncludeFormattedMessage = true;
			logging.IncludeScopes = true;
		});

		builder.Services
			.AddOpenTelemetry()
			.ConfigureResource(resource => resource.AddService(serviceName: "Roslynk"))
			.WithTracing(tracing => tracing
				.AddSource(RoslynkActivitySource.Name)
				.AddAspNetCoreInstrumentation())
			.WithMetrics(metrics => metrics
				.AddMeter(RoslynkMeter.Name)
				.AddAspNetCoreInstrumentation())
			.UseOtlpExporter();

		return builder;
	}
}
