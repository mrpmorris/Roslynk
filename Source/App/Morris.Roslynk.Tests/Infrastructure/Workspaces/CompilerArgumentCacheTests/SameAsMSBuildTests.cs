using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Workspaces;
using Xunit.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

/// <summary>
/// A solution loaded from captured compiler arguments is the solution MSBuildWorkspace loads: the same projects,
/// files, references, analyzers and options, and the same compiler diagnostics, with generators run. Every fixture is
/// a fresh copy, restored and not built, so nothing depends on compiled outputs.
/// </summary>
public class SameAsMSBuildTests(ITestOutputHelper output) : MutatingTestBase
{
	public static TheoryData<string, string> Fixtures => new()
	{
		{ "SimpleSolution", "SimpleSolution.slnx" },
		{ "BrokenSolution", "BrokenSolution.slnx" },
		{ "GeneratorSolution", "GeneratorSolution.slnx" },
		{ "RazorSolution", "RazorSolution.slnx" },
		{ "CshtmlSolution", "CshtmlSolution.slnx" },
		{ "RazorMultiProjectSolution", "RazorMultiProjectSolution.slnx" },
		{ "CodeStyleSolution", "CodeStyleSolution.slnx" },
		{ "ConditionalSolution", "ConditionalSolution.slnx" },
		{ "LocalFunctionSolution", "LocalFunctionSolution.slnx" },
		{ "MultiTargetSolution", Path.Combine("src", "MultiTarget.slnx") },
	};

	[Theory]
	[MemberData(nameof(Fixtures))]
	public async Task WhenAFixtureLoadsAgain_ThenTheCachedLoadMatchesMSBuilds(string fixture, string solutionFile) =>
		await AssertCachedLoadMatchesAsync(Scratch(() => TestSolutions.CreateScratchFixture(fixture, solutionFile)));

	[Fact]
	public async Task WhenAProjectReferencesAMultiTargetedOne_ThenTheCachedLoadMatchesMSBuilds() =>
		await AssertCachedLoadMatchesAsync(Scratch(() => TestSolutions.CreateScratchLinkedSolution()));

	[Fact]
	public async Task WhenAProjectCompilesNothing_ThenTheCachedLoadMatchesMSBuilds() =>
		await AssertCachedLoadMatchesAsync(Scratch(() => TestSolutions.CreateScratchLinkedSolution(withNoTargetsProject: true)));

	[Fact]
	public async Task WhenTheGeneratorIsBuilt_ThenTheCachedLoadMatchesMSBuilds() =>
		await AssertCachedLoadMatchesAsync(Scratch(() => TestSolutions.CreateScratchGeneratorSolution()));

	[Theory]
	[MemberData(nameof(Fixtures))]
	public async Task WhenTheLoadIsLogged_ThenMSBuildLoadsTheSameSolutionAndEveryProjectIsCaptured(string fixture, string solutionFile)
	{
		string solutionPath = Scratch(() => TestSolutions.CreateScratchFixture(fixture, solutionFile));
		MsBuildRegistrar.EnsureRegistered();

		using CompilerArgumentCapture capture = CompilerArgumentCapture.Begin(TestCaches.New().Root);
		using MSBuildWorkspace logged = MSBuildWorkspace.Create();
		Solution withLog = await capture.OpenSolutionAsync(logged, solutionPath, new Progress<ProjectLoadProgress>(), CancellationToken.None);

		using MSBuildWorkspace plain = MSBuildWorkspace.Create();
		Solution withoutLog = await plain.OpenSolutionAsync(solutionPath);

		AssertNoDifferences(ProjectFacts.Describe(withoutLog), "without log", ProjectFacts.Describe(withLog), "with log");
		CapturedLoad captured = capture.Read();
		Assert.Equal(withLog.Projects.Count(project => project.Documents.Any()), captured.Arguments.Count);
		Assert.All(captured.Arguments, arguments => Assert.NotNull(arguments.ProjectAssetsFile));
		Assert.Single(captured.SdkFolders);
	}

	private async Task AssertCachedLoadMatchesAsync(string solutionPath)
	{
		CompilerArgumentCache cache = TestCaches.New();

		Stopwatch cold = Stopwatch.StartNew();
		using SolutionWorkspace fromMSBuild = await SolutionWorkspace.LoadAsync(solutionPath, cache);
		cold.Stop();
		Assert.False(fromMSBuild.LoadedFromCache);
		Assert.True(cache.LastStoreReason == "", $"not stored: {cache.LastStoreReason}");

		Stopwatch warm = Stopwatch.StartNew();
		using SolutionWorkspace fromCache = await SolutionWorkspace.LoadAsync(solutionPath, cache);
		warm.Stop();
		Assert.True(fromCache.LoadedFromCache, cache.LastLoadReason);
		output.WriteLine($"{Path.GetFileName(solutionPath)}: through MSBuild {cold.Elapsed.TotalSeconds:F2}s, from the cache {warm.Elapsed.TotalSeconds:F2}s");

		AssertNoDifferences(ProjectFacts.Describe(fromMSBuild.Solution), "MSBuild", ProjectFacts.Describe(fromCache.Solution), "cache");
		Assert.Equal(fromMSBuild.LoadDiagnostics.Order(), fromCache.LoadDiagnostics.Order());
		Assert.Equal(fromMSBuild.LoadWarnings.Order(), fromCache.LoadWarnings.Order());
		AssertNoDifferences(await CompilerDiagnosticsAsync(fromMSBuild.Solution), "MSBuild", await CompilerDiagnosticsAsync(fromCache.Solution), "cache");
	}

	/// <summary>Every project's compiler diagnostics, generators run, one line each.</summary>
	private static async Task<string[]> CompilerDiagnosticsAsync(Solution solution)
	{
		var lines = new List<string>();
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync();
			if (compilation is null)
				continue;

			lines.Add($"{project.Name} trees {compilation.SyntaxTrees.Count()}");
			lines.AddRange(compilation.GetDiagnostics()
				.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
				.Select(diagnostic => $"{project.Name} {diagnostic.Id} {diagnostic.Severity} {diagnostic.Location.GetLineSpan()}"));
		}

		lines.Sort(StringComparer.Ordinal);
		return [.. lines];
	}

	private static void AssertNoDifferences(string[] expected, string expectedName, string[] actual, string actualName)
	{
		string[] differences = ProjectFacts.Differences(expected, expectedName, actual, actualName);
		Assert.True(differences.Length == 0, string.Join('\n', differences));
	}
}
