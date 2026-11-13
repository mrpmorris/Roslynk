namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// One discovery outcome: an error-severity OpenTelemetry record at startup. <see cref="Reason"/> is
/// free text; line breaks are flattened at creation so the value can never corrupt a log line.
/// <see cref="Stage"/> is null for rejections with no dedicated stage — a loose entry assembly in the
/// plugins root, whose corrective action is its own diagnosis.
/// </summary>
public sealed record PluginDiscoveryRecord(
	PluginFailureStage? Stage,
	string Reason,
	string? PluginName = null,
	string? PluginFolder = null)
{
	public static PluginDiscoveryRecord Create(PluginFailureStage? stage, string reason, string? pluginName = null, string? pluginFolder = null) =>
		new(stage, reason.ReplaceLineEndings(" "), pluginName, pluginFolder);
}

/// <summary>A plugin that passed discovery and is ready for loading.</summary>
public sealed record PluginCandidate(
	string PluginName,
	string PluginFolderPath,
	string EntryAssemblyPath);

/// <summary>The full discovery outcome: loadable candidates plus every record, in examination order.</summary>
public sealed record PluginDiscoveryResult(
	IReadOnlyList<PluginCandidate> Candidates,
	IReadOnlyList<PluginDiscoveryRecord> Records);
