using Morris.Roslynk.Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Wires OpenTelemetry traces, metrics, and logs through OTLP exporters that read their endpoint from
/// the standard <c>OTEL_EXPORTER_OTLP_*</c> configuration; Roslynk writes no transport code. Traces go
/// through <see cref="McpTransportNoiseFilterExporter"/> first, so the dashboard shows tool calls and
/// their engine spans without the MCP protocol chatter around them.
/// <para>
/// OpenTelemetry is only wired when an endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>).
/// With no endpoint there is nothing to export to, so nothing is wired and nothing is emitted; the
/// server runs exactly as before.
/// </para>
/// </summary>
public static class OpenTelemetryExtensions
{
	public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
	public const string OtlpProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
	public const string OtlpHeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";

	public static WebApplicationBuilder AddRoslynkObservability(this WebApplicationBuilder builder)
	{
		string? otlpEndpoint = builder.Configuration[OtlpEndpointKey];
		if (string.IsNullOrWhiteSpace(otlpEndpoint))
			return builder;

		builder.Logging.AddOpenTelemetry(logging =>
		{
			logging.IncludeFormattedMessage = true;
			logging.IncludeScopes = true;
			logging.AddOtlpExporter();
		});

		builder.Services
			.AddOpenTelemetry()
			.ConfigureResource(resource => resource.AddService(serviceName: "Roslynk"))
			.WithTracing(tracing => tracing
				.AddSource(RoslynkActivitySource.Name)
				.AddSource(McpToolTraceNaming.McpActivitySourceName)
				.AddAspNetCoreInstrumentation(instrumentation =>
					instrumentation.EnrichWithHttpRequest = McpToolTraceNaming.RecordSessionId)
				.AddProcessor(new McpToolSpanNameProcessor())
				.AddOtlpExporterWithNoiseFilter(otlpEndpoint, builder.Configuration[OtlpProtocolKey], builder.Configuration[OtlpHeadersKey]))
			.WithMetrics(metrics => metrics
				.AddMeter(RoslynkMeter.Name)
				.AddAspNetCoreInstrumentation()
				.AddOtlpExporter());

		return builder;
	}

	/// <summary>
	/// Registers the OTLP trace exporter behind the noise filter. The OTLP options are built by hand —
	/// rather than via AddOtlpExporter — because wrapping the exporter in the filter is only possible
	/// before the SDK owns it. Protocol defaults to gRPC, matching OtlpExporterOptions.
	/// </summary>
	private static TracerProviderBuilder AddOtlpExporterWithNoiseFilter(
		this TracerProviderBuilder builder, string endpoint, string? protocol, string? headers)
	{
		var options = new OtlpExporterOptions { Endpoint = new Uri(endpoint) };
		if (protocol is not null && protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
			options.Protocol = OtlpExportProtocol.HttpProtobuf;
		if (headers is not null)
			options.Headers = headers;

		return builder.AddProcessor(new BatchActivityExportProcessor(
			new McpTransportNoiseFilterExporter(new OtlpTraceExporter(options))));
	}
}
