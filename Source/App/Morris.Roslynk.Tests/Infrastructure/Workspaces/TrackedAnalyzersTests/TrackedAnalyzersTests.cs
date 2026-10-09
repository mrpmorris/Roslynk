using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.TrackedAnalyzersTests;

public class TrackedAnalyzersTests
{
	[Fact]
	public async Task WhenNoTrackedDllChanged_ThenARefreshPublishesNothing()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Guid before = instance.CurrentModel.Id;

		await instance.RefreshTrackedAnalyzersAsync();

		Assert.Equal(before, instance.CurrentModel.Id);
	}

	[Fact]
	public async Task WhenATrackedDllIsRebuilt_ThenOnlyItsConsumersGetAFreshReferenceAndANewSemanticVersion()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		string dll = GeneratorDllPath(solutionPath);
		AnalyzerFileReference before = GeneratorReference(instance, dll);
		VersionStamp versionBefore = await Consumer(instance).GetDependentSemanticVersionAsync();
		IReadOnlyList<AnalyzerReference> generatorProjectBefore = Project(instance, "GeneratorLib").AnalyzerReferences;

		// A write-time change stands in for a rebuild: both are the stamp change the refresh compares.
		File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
		await instance.RefreshTrackedAnalyzersAsync();

		Assert.NotSame(before, GeneratorReference(instance, dll));
		Assert.Single(Consumer(instance).AnalyzerReferences, reference => reference is AnalyzerFileReference file && PathsEqual(file.FullPath, dll));
		Assert.NotEqual(versionBefore, await Consumer(instance).GetDependentSemanticVersionAsync());
		Assert.Same(generatorProjectBefore, Project(instance, "GeneratorLib").AnalyzerReferences);
	}

	[Fact]
	public async Task WhenTheSwapFails_ThenTheNextRefreshRetriesIt()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		TestSolutions.BuildScratchGenerator(solutionPath);

		await workspace.TrackedAnalyzers.RefreshAsync(_ => throw new InvalidOperationException("The write faulted."));
		Solution? attached = null;
		await workspace.TrackedAnalyzers.RefreshAsync(transform =>
		{
			attached = transform(workspace.Solution);
			return Task.CompletedTask;
		});

		Assert.NotNull(attached);
		Project consumer = attached.Projects.Single(project => project.Name == "ConsumerLib");
		Assert.Contains(consumer.AnalyzerReferences, reference => reference is AnalyzerFileReference file && PathsEqual(file.FullPath, GeneratorDllPath(solutionPath)));
		Assert.DoesNotContain(workspace.LoadDiagnostics, d => d.Contains("GeneratorLib.dll", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenTheFirstLoadThrows_ThenItIsReportedAndTheNextRefreshStillApplies()
	{
		string directory = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string dll = Path.Combine(directory, "Gen.dll");
		await File.WriteAllTextAsync(dll, "stub");
		try
		{
			// Stands in for File.Copy racing the build's write: the stamp says the DLL exists, the first load
			// throws, and a later one (the build done) succeeds.
			int loads = 0;
			var entry = new TrackedAnalyzer(dll, null, (path, _, _) => ++loads == 1
				? throw new IOException("copy raced the build")
				: new AnalyzerFileReference(path, new ShadowCopyingAnalyzerAssemblyLoader(directory)));
			var subject = new TrackedAnalyzers([entry]);

			int applies = 0;
			await subject.RefreshAsync(_ => { applies++; return Task.CompletedTask; });

			Assert.Equal(0, applies);
			string message = Assert.Single(subject.Messages);
			Assert.Contains("Analyzer load failed", message, StringComparison.Ordinal);
			Assert.Contains("copy raced the build", message, StringComparison.Ordinal);

			await subject.RefreshAsync(_ => { applies++; return Task.CompletedTask; });

			Assert.Equal(1, applies);
			Assert.Empty(subject.Messages);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static AnalyzerFileReference GeneratorReference(RoslynInstance instance, string dll) =>
		Consumer(instance).AnalyzerReferences.OfType<AnalyzerFileReference>().Single(file => PathsEqual(file.FullPath, dll));

	private static Project Consumer(RoslynInstance instance) => Project(instance, "ConsumerLib");

	private static Project Project(RoslynInstance instance, string name) =>
		instance.CurrentSolution.Projects.Single(project => project.Name == name);

	private static bool PathsEqual(string left, string right) =>
		string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

	private static string GeneratorDllPath(string solutionPath) => Path.Combine(
		Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "bin", "Debug", "netstandard2.0", "GeneratorLib.dll");
}
