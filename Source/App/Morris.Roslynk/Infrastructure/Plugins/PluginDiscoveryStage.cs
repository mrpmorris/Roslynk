namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The stages a plugin candidate can be rejected in, plus the version-skew warning. Tokens are the
/// kebab-case strings the OTEL record and the GET /plugins payload carry.
/// </summary>
public enum PluginDiscoveryStage
{
	FolderNameInvalid,
	EntryDllMissing,
	NameMismatch,
	DuplicateName,
	UnsupportedVersion,
	MissingApiVersionAttribute,
	Malformed,
	UnreadableOrLocked,
	DependencyUnresolved,
	FactoryFailure,
	RoslynVersionSkew,
}

public static class PluginDiscoveryStageExtensions
{
	public static string ToStageToken(this PluginDiscoveryStage stage) =>
		stage switch
		{
			PluginDiscoveryStage.FolderNameInvalid => "folder-name-invalid",
			PluginDiscoveryStage.EntryDllMissing => "entry-dll-missing",
			PluginDiscoveryStage.NameMismatch => "name-mismatch",
			PluginDiscoveryStage.DuplicateName => "duplicate-name",
			PluginDiscoveryStage.UnsupportedVersion => "unsupported-version",
			PluginDiscoveryStage.MissingApiVersionAttribute => "missing-attribute",
			PluginDiscoveryStage.Malformed => "malformed",
			PluginDiscoveryStage.UnreadableOrLocked => "unreadable-or-locked",
			PluginDiscoveryStage.DependencyUnresolved => "dependency-unresolved",
			PluginDiscoveryStage.FactoryFailure => "factory-failure",
			PluginDiscoveryStage.RoslynVersionSkew => "roslyn-version-skew",
			_ => throw new NotSupportedException($"Unknown discovery stage: {stage}"),
		};
}
