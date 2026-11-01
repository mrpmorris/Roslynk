using System.Runtime.CompilerServices;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Clears scratch directories left behind by earlier test runs. Every scratch solution a write test
/// creates lives under <c>%TEMP%/roslynk-tests/&lt;guid&gt;</c> and is never reused, so a run that ended
/// without cleaning up only costs the next run time: once thousands accumulate, MSBuild and the
/// filesystem take measurably longer to work through them.
/// <para>
/// This runs when the test assembly loads, before any test has created a directory, so it only clears
/// debris from previous runs. It assumes one test process at a time: a second suite running
/// concurrently would have its live directories deleted. Failures are ignored because a directory a
/// previous run's workspace still holds open cannot be removed, and that is not an error worth failing
/// a test run over.
/// </para>
/// </summary>
internal static class ScratchDirectorySweeper
{
	private const string RootName = "roslynk-tests";

	[ModuleInitializer]
	internal static void SweepPreviousRuns()
	{
		string root = Path.Combine(Path.GetTempPath(), RootName);
		if (!Directory.Exists(root))
			return;

		foreach (string directory in Directory.EnumerateDirectories(root))
		{
			try
			{
				Directory.Delete(directory, recursive: true);
			}
			catch (IOException)
			{
				// Still held open by a workspace from an earlier run; leave it for the next sweep.
			}
			catch (UnauthorizedAccessException)
			{
				// Same: a locked file inside the directory.
			}
		}
	}
}
