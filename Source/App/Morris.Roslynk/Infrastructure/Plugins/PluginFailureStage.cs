namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The stages a plugin candidate can be rejected in. Tokens are the kebab-case strings the
/// error-severity OpenTelemetry record carries. Deliberately absent: any version stage — no
/// version metadata exists to reject on.
/// </summary>
public enum PluginFailureStage
{
	FolderNameInvalid,
	EntryDllMissing,
	NameMismatch,
	DuplicateName,
	UnreadableOrLocked,
	DependencyUnresolved,
	StartupTimeout,
	FactoryFailure,
	SubscriptionFailure,
	FactoryTypeInvalid,
}

public static class PluginFailureStageExtensions
{
	public static string ToStageToken(this PluginFailureStage stage) =>
		stage switch
		{
			PluginFailureStage.FolderNameInvalid => "folder-name-invalid",
			PluginFailureStage.EntryDllMissing => "entry-dll-missing",
			PluginFailureStage.NameMismatch => "name-mismatch",
			PluginFailureStage.DuplicateName => "duplicate-name",
			PluginFailureStage.UnreadableOrLocked => "unreadable-or-locked",
			PluginFailureStage.DependencyUnresolved => "dependency-unresolved",
			PluginFailureStage.StartupTimeout => "startup-timeout",
			PluginFailureStage.FactoryFailure => "factory-failure",
			PluginFailureStage.SubscriptionFailure => "subscription-failure",
			PluginFailureStage.FactoryTypeInvalid => "factory-type-invalid",
			_ => throw new NotSupportedException($"Unknown plugin failure stage: {stage}"),
		};
}
