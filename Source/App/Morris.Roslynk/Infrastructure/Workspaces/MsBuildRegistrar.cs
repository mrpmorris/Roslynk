using Microsoft.Build.Locator;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Registers the SDK's MSBuild with <see cref="MSBuildLocator"/> exactly once, before any
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> is created. This is the single most
/// common thing that breaks a Roslyn host on a fresh machine, so it is centralised and idempotent.
/// </summary>
public static class MsBuildRegistrar
{
	private static readonly object SyncRoot = new();
	private static bool Registered;

	public static void EnsureRegistered()
	{
		if (Registered)
			return;

		lock (SyncRoot)
		{
			if (Registered)
				return;

			if (!MSBuildLocator.IsRegistered)
				MSBuildLocator.RegisterDefaults();

			Registered = true;
		}
	}
}
