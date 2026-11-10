using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>One assembly the probed entry assembly references, for the Roslyn version-skew check.</summary>
public sealed record PluginAssemblyReference(string Name, string Version);

/// <summary>
/// The outcome of reflecting over a candidate entry assembly without loading it for execution.
/// <see cref="ApiVersion"/> is set exactly when the probe succeeded; on failure <see cref="Stage"/>
/// and <see cref="Reason"/> name the stage and the cause.
/// </summary>
public sealed record PluginProbeResult(
	ApiVersion? ApiVersion,
	PluginDiscoveryStage? Stage,
	string? Reason,
	IReadOnlyList<PluginAssemblyReference> ReferencedAssemblies)
{
	private static readonly PluginAssemblyReference[] NoReferences = [];

	public bool IsSuccess => ApiVersion is not null;

	/// <summary>The failure stage; a failed result always carries one.</summary>
	public PluginDiscoveryStage FailureStage =>
		Stage ?? throw new InvalidOperationException($"{nameof(PluginProbeResult)} has no failure stage");

	public static PluginProbeResult Succeeded(ApiVersion apiVersion, IReadOnlyList<PluginAssemblyReference> referencedAssemblies) =>
		new(apiVersion, null, null, referencedAssemblies);

	public static PluginProbeResult Failed(PluginDiscoveryStage stage, string reason) =>
		new(null, stage, reason, NoReferences);

	public static PluginProbeResult Failed(PluginDiscoveryStage stage, string reason, IReadOnlyList<PluginAssemblyReference> referencedAssemblies) =>
		new(null, stage, reason, referencedAssemblies);
}
