namespace Morris.Roslynk.Infrastructure.Plugins;

public enum PluginDiscoverySeverity
{
	Error,
	Warning,
}

/// <summary>
/// One discovery outcome: an OpenTelemetry record at startup and a row of the GET /plugins payload.
/// <see cref="Reason"/> is free text; line breaks are flattened at creation so the value can never
/// corrupt a log line or a report row.
/// </summary>
public sealed record PluginDiscoveryRecord(
	PluginDiscoveryStage Stage,
	PluginDiscoverySeverity Severity,
	string Reason,
	string? PluginName = null,
	string? PluginFolder = null,
	string? ContractVersion = null);
