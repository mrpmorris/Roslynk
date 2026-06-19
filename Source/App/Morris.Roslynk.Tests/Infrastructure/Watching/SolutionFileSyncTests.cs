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

	[Fact]
	public async Task WhenANonSourceNonBuildFileChangesOutsideBinObj_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string asset = Path.Combine(projectDir, "appsettings.json");
		await File.WriteAllTextAsync(asset, "{}");

		await subject.OnFileChangedAsync(asset);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenAFileUnderObjOrBinChanges_ThenItIsIgnored()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string objArtifact = Path.Combine(projectDir, "obj", "Debug", "Generated.cs");

		await subject.OnFileChangedAsync(objArtifact);

		Assert.False(instance.IsDirty);
		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenANewSourceFileIsAddedUnderADefaultGlobProject_ThenItIsFoldedInWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string added = Path.Combine(projectDir, "Added.cs");
		await File.WriteAllTextAsync(added, "namespace SimpleLibrary; public class Added { }");

		await subject.OnFileChangedAsync(added);

		Assert.False(instance.IsDirty);
		Assert.NotEmpty(instance.CurrentSolution.GetDocumentIdsWithFilePath(added));
	}

	[Fact]
	public async Task WhenAKnownSourceFileIsDeleted_ThenItIsRemovedWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		Assert.NotEmpty(instance.CurrentSolution.GetDocumentIdsWithFilePath(greeter));
		File.Delete(greeter);

		await subject.OnFileChangedAsync(greeter);

		Assert.False(instance.IsDirty);
		Assert.Empty(instance.CurrentSolution.GetDocumentIdsWithFilePath(greeter));
	}

	[Fact]
	public async Task WhenANewSourceFileIsAddedUnderAProjectWithExplicitCompileItems_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectFile = FindFile(solutionPath, "*.csproj");
		string optedOut = (await File.ReadAllTextAsync(projectFile))
			.Replace("</Project>", "  <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>\n</Project>");
		await File.WriteAllTextAsync(projectFile, optedOut);

		string added = Path.Combine(Path.GetDirectoryName(projectFile)!, "AddedExplicit.cs");
		await File.WriteAllTextAsync(added, "namespace SimpleLibrary; public class AddedExplicit { }");

		await subject.OnFileChangedAsync(added);

		Assert.True(instance.IsDirty);
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
