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
	private static readonly Lazy<string> ReferencesSolutionPath = new(() => Prepare("ReferencesSolution", "ReferencesSolution.slnx"));
	private static readonly Lazy<string> ConditionalSolutionPath = new(() => Prepare("ConditionalSolution", "ConditionalSolution.slnx"));
	private static readonly Lazy<string> GeneratorSolutionPath = new(() =>
	{
		string path = Prepare("GeneratorSolution", "GeneratorSolution.slnx");

		// The consumer references the generator project as an analyzer only; the workspace's design-time
		// build never compiles it, so the generator DLL must be built before the solution loads.
		Build(Path.Combine(Path.GetDirectoryName(path)!, "GeneratorLib", "GeneratorLib.csproj"));
		return path;
	});

	/// <summary>A clean single-project solution.</summary>
	public static string Simple => SimpleSolution.Value;

	/// <summary>A single-project solution containing a deliberate CS0029 compile error.</summary>
	public static string Broken => BrokenSolution.Value;

	/// <summary>A Razor Class Library with a component whose handler is wired only in markup.</summary>
	public static string Razor => RazorSolutionPath.Value;

	/// <summary>A two-file solution with an interface referenced many ways, for testing reference grouping.</summary>
	public static string References => ReferencesSolutionPath.Value;

	/// <summary>A single-project solution whose method is called in both the #if DEBUG and #else branches.</summary>
	public static string Conditional => ConditionalSolutionPath.Value;

	/// <summary>A consumer using a type emitted by a project-referenced source generator (built on first use).</summary>
	public static string Generator => GeneratorSolutionPath.Value;

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

	private static void Restore(string solutionPath) => RunDotnet("restore", solutionPath);

	private static void Build(string projectPath) => RunDotnet("build", projectPath);

	private static void RunDotnet(string verb, string path)
	{
		var startInfo = new ProcessStartInfo("dotnet", $"{verb} \"{path}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Failed to start 'dotnet {verb}'.");
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"'dotnet {verb}' on '{path}' failed:\n{process.StandardOutput.ReadToEnd()}\n{process.StandardError.ReadToEnd()}");
	}

	/// <summary>
	/// Copies the SimpleSolution fixture (source only) to a fresh temp directory and restores it, so
	/// write tests can modify files without dirtying the committed fixture.
	/// </summary>
	public static string CreateScratchSimpleSolution() => CreateScratch("SimpleSolution", "SimpleSolution.slnx");

	/// <summary>A writable scratch copy of the ConditionalSolution fixture, for tests that rename/edit it.</summary>
	public static string CreateScratchConditionalSolution() => CreateScratch("ConditionalSolution", "ConditionalSolution.slnx");

	/// <summary>A writable scratch copy of the RazorSolution fixture, for tests that rename/edit its .razor files.</summary>
	public static string CreateScratchRazorSolution() => CreateScratch("RazorSolution", "RazorSolution.slnx");

	/// <summary>
	/// A scratch copy of the GeneratorSolution fixture whose generator DLL has deliberately NOT been
	/// built, for tests asserting how an unloadable analyzer reference is reported.
	/// </summary>
	public static string CreateScratchGeneratorSolutionWithoutBuiltGenerator() => CreateScratch("GeneratorSolution", "GeneratorSolution.slnx");

	private static string CreateScratch(string fixtureName, string solutionFile)
	{
		string sourceDir = Path.Combine(FindTestFixturesRoot(), fixtureName);
		string destDir = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), fixtureName);
		CopyExcludingBuildOutput(sourceDir, destDir);

		string solutionPath = Path.Combine(destDir, solutionFile);
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
