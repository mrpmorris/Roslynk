using System.Diagnostics;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Locates the checked-in fixture solutions under <c>TestFixtures/</c> and restores them on demand, so
/// the loading tests work from a fresh clone where <c>obj/</c> is not committed.
/// </summary>
internal static class TestSolutions
{
	private static readonly Lazy<string> SimpleSolution = new(() => Prepare("SimpleSolution", "SimpleSolution.slnx"));
	private static readonly Lazy<string> BrokenSolution = new(() => Prepare("BrokenSolution", "BrokenSolution.slnx"));

	/// <summary>A clean single-project solution.</summary>
	public static string Simple => SimpleSolution.Value;

	/// <summary>A single-project solution containing a deliberate CS0029 compile error.</summary>
	public static string Broken => BrokenSolution.Value;

	private static string Prepare(params string[] relativeParts)
	{
		string path = Path.Combine([FindTestFixturesRoot(), .. relativeParts]);
		Restore(path);
		return path;
	}

	private static string FindTestFixturesRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "TestFixtures");
			if (Directory.Exists(candidate))
				return candidate;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the TestFixtures folder above the test assembly.");
	}

	private static void Restore(string solutionPath)
	{
		var startInfo = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start 'dotnet restore'.");
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"Restoring '{solutionPath}' failed:\n{process.StandardError.ReadToEnd()}");
	}
}
