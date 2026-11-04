using System.Runtime.CompilerServices;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

internal static class TestCaches
{
	/// <summary>
	/// Every load in this test run caches under the run's own temp folder, not the user's: a fixture loaded by
	/// several tests is then loaded from the cache after the first, as it would be in use.
	/// </summary>
	[ModuleInitializer]
	internal static void UseATempFolderForTheRun() =>
		CompilerArgumentCache.Default = new CompilerArgumentCache(NewCacheRoot());

	/// <summary>An empty cache of a test's own.</summary>
	public static CompilerArgumentCache New() => new(NewCacheRoot());

	private static string NewCacheRoot()
	{
		string root = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), "compiler-arguments");
		ScratchDirectories.Track(root);
		return root;
	}
}
