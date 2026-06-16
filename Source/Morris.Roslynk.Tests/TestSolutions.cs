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
	private static readonly Lazy<string> RazorSolutionPath = new(() => Prepare("RazorSolution", "RazorSolution.slnx"));
	private static readonly Lazy<string> MultiTargetSolutionPath = new(() => Prepare("MultiTargetSolution", "MultiTargetSolution.slnx"));
	private static readonly Lazy<string> ReferencesSolutionPath = new(() => Prepare("ReferencesSolution", "ReferencesSolution.slnx"));

	/// <summary>A clean single-project solution.</summary>
	public static string Simple => SimpleSolution.Value;

	/// <summary>A single-project solution containing a deliberate CS0029 compile error.</summary>
	public static string Broken => BrokenSolution.Value;

	/// <summary>A Razor Class Library with a component whose handler is wired only in markup.</summary>
	public static string Razor => RazorSolutionPath.Value;

	/// <summary>A net8.0;net10.0 multi-targeted project with a CS0029 present only in the net8.0 compilation.</summary>
	public static string MultiTarget => MultiTargetSolutionPath.Value;

	/// <summary>A two-file solution with an interface referenced many ways, for testing reference grouping.</summary>
	public static string References => ReferencesSolutionPath.Value;

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

	/// <summary>
	/// Copies the SimpleSolution fixture (source only) to a fresh temp directory and restores it, so
	/// write tests can modify files without dirtying the committed fixture.
	/// </summary>
	public static string CreateScratchSimpleSolution()
	{
		string sourceDir = Path.Combine(FindTestFixturesRoot(), "SimpleSolution");
		string destDir = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), "SimpleSolution");
		CopyExcludingBuildOutput(sourceDir, destDir);

		string solutionPath = Path.Combine(destDir, "SimpleSolution.slnx");
		Restore(solutionPath);
		return solutionPath;
	}

	private static void CopyExcludingBuildOutput(string sourceDir, string destDir)
	{
		foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(sourceDir, file);
			if (relative.Contains($"obj{Path.DirectorySeparatorChar}") || relative.Contains($"bin{Path.DirectorySeparatorChar}"))
				continue;

			string target = Path.Combine(destDir, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, target, overwrite: true);
		}
	}
}
