using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>One assembly the probed entry assembly references, for the Roslyn version-skew check.</summary>
public sealed record PluginAssemblyReference(string Name, string Version);

/// <summary>
/// The outcome of reflecting over a candidate entry assembly without loading it for execution.
/// <see cref="ApiVersion"/> is set exactly when the probe succeeded; on failure <see cref="Stage"/>
/// and <see cref="Reason"/> name the stage and the cause. <see cref="EntryVersion"/> is the probed
/// assembly's own version, when the probe read one — the version-out-of-range record carries it.
/// </summary>
public sealed record PluginProbeResult(
	ApiVersion? ApiVersion,
	PluginDiscoveryStage? Stage,
	string? Reason,
	IReadOnlyList<PluginAssemblyReference> ReferencedAssemblies,
	Version? EntryVersion = null)
{
	private static readonly PluginAssemblyReference[] NoReferences = [];

	public bool IsSuccess => ApiVersion is not null;

	/// <summary>The failure stage; a failed result always carries one.</summary>
	public PluginDiscoveryStage FailureStage =>
		Stage ?? throw new InvalidOperationException($"{nameof(PluginProbeResult)} has no failure stage");

	public static PluginProbeResult Succeeded(ApiVersion apiVersion, IReadOnlyList<PluginAssemblyReference> referencedAssemblies, Version? entryVersion) =>
		new(apiVersion, null, null, referencedAssemblies, entryVersion);

	public static PluginProbeResult Failed(PluginDiscoveryStage stage, string reason) =>
		new(null, stage, reason, NoReferences);

	public static PluginProbeResult Failed(PluginDiscoveryStage stage, string reason, IReadOnlyList<PluginAssemblyReference> referencedAssemblies) =>
		new(null, stage, reason, referencedAssemblies);

	public static PluginProbeResult Failed(PluginDiscoveryStage stage, string reason, IReadOnlyList<PluginAssemblyReference> referencedAssemblies, Version? entryVersion) =>
		new(null, stage, reason, referencedAssemblies, entryVersion);
}
