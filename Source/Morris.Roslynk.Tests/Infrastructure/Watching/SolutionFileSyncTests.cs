using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Watching;

namespace Morris.Roslynk.Tests.Infrastructure.Watching;

public class SolutionFileSyncTests
{
	[Fact]
	public async Task WhenASourceFileChangesOnDisk_ThenTheLoadedSnapshotPicksItUp()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string modified = (await File.ReadAllTextAsync(greeter)) + "\n// changed on disk\n";
		await File.WriteAllTextAsync(greeter, modified);

		await subject.OnFileChangedAsync(greeter);

		string snapshotText = await ReadDocumentTextAsync(instance.CurrentSolution, greeter);
		Assert.Contains("changed on disk", snapshotText);
	}

	[Fact]
	public async Task WhenAnUnchangedSourceFileEventArrives_ThenTheSnapshotIsNotReplaced()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		await subject.OnFileChangedAsync(FindFile(solutionPath, "Greeter.cs"));

		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenAProjectFileChangesOnDisk_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectFile = FindFile(solutionPath, "*.csproj");
		string edited = (await File.ReadAllTextAsync(projectFile)).Replace("</Project>", "  <!-- touched -->\n</Project>");
		await File.WriteAllTextAsync(projectFile, edited);

		await subject.OnFileChangedAsync(projectFile);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenADirtyInstanceIsRequestedAgain_ThenItIsReloadedFresh()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectFile = FindFile(solutionPath, "*.csproj");
		string edited = (await File.ReadAllTextAsync(projectFile)).Replace("</Project>", "  <!-- touched -->\n</Project>");
		await File.WriteAllTextAsync(projectFile, edited);
		await subject.OnFileChangedAsync(projectFile);

		RoslynInstance reloaded = await registry.GetOrAddAsync(solutionPath);

		Assert.NotSame(instance, reloaded);
		Assert.False(reloaded.IsDirty);
	}

	private static async Task<string> ReadDocumentTextAsync(Solution solution, string path)
	{
		DocumentId id = solution.GetDocumentIdsWithFilePath(path).First();
		Document document = solution.GetDocument(id)!;
		return (await document.GetTextAsync()).ToString();
	}

	private static string FindFile(string solutionPath, string pattern) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, pattern, SearchOption.AllDirectories).First();
}
