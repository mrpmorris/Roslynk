using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>A plugin that passed discovery and is ready for loading.</summary>
public sealed record PluginCandidate(
	string PluginName,
	string PluginFolderPath,
	string EntryAssemblyPath,
	ApiVersion ApiVersion);

/// <summary>The full discovery outcome: loadable candidates plus every record, in examination order.</summary>
public sealed record PluginDiscoveryResult(
	IReadOnlyList<PluginCandidate> Candidates,
	IReadOnlyList<PluginDiscoveryRecord> Records);
