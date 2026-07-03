using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Watching;
using Morris.Roslynk.Infrastructure.Writing;

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
	public async Task WhenTheServersOwnStagingFileEventsArrive_ThenTheyAreIgnored()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		// A staging temp still on disk (the event raced the commit) and a backup already cleaned up
		// (the usual case: the debounced flush runs after the swap deleted it) must both be ignored.
		string greeter = FindFile(solutionPath, "Greeter.cs");
		string stagedTemp = greeter + ".roslynk.tmp";
		await File.WriteAllTextAsync(stagedTemp, "// staged content");

		await subject.OnFileChangedAsync(stagedTemp);
		await subject.OnFileChangedAsync(greeter + ".roslynk.bak");

		Assert.False(instance.IsDirty);
		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenApplyPipelineWritesADocument_ThenTheResultingWatcherEventsDoNotMarkTheInstanceDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		await ApplyEditThroughPipelineAsync(instance, greeter, "// applied through the pipeline");
		Solution afterApply = instance.CurrentSolution;

		// Replay the events a committed atomic write raises: the staging siblings and the target itself.
		await subject.OnFileChangedAsync(greeter + ".roslynk.tmp");
		await subject.OnFileChangedAsync(greeter + ".roslynk.bak");
		await subject.OnFileChangedAsync(greeter);

		Assert.False(instance.IsDirty);
		Assert.Same(afterApply, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenAnExternalEditFollowsAnApplyPipelineWrite_ThenTheInstanceIsStillMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		await ApplyEditThroughPipelineAsync(instance, greeter, "// applied through the pipeline");
		await subject.OnFileChangedAsync(greeter + ".roslynk.tmp");
		await subject.OnFileChangedAsync(greeter + ".roslynk.bak");
		await subject.OnFileChangedAsync(greeter);
		Assert.False(instance.IsDirty);

		string projectFile = FindFile(solutionPath, "*.csproj");
		string edited = (await File.ReadAllTextAsync(projectFile)).Replace("</Project>", "  <!-- external edit -->\n</Project>");
		await File.WriteAllTextAsync(projectFile, edited);

		await subject.OnFileChangedAsync(projectFile);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenAnExternalSourceEditFollowsAnApplyPipelineWrite_ThenItIsStillFoldedIn()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		await ApplyEditThroughPipelineAsync(instance, greeter, "// applied through the pipeline");
		await subject.OnFileChangedAsync(greeter);

		string external = (await File.ReadAllTextAsync(greeter)) + "\n// external edit\n";
		await File.WriteAllTextAsync(greeter, external);

		await subject.OnFileChangedAsync(greeter);

		Assert.False(instance.IsDirty);
		string snapshotText = await ReadDocumentTextAsync(instance.CurrentSolution, greeter);
		Assert.Contains("external edit", snapshotText);
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

	[Fact]
	public async Task WhenACsAnalyzerAdditionalFileIsModified_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string additional = await AddAnalyzerAdditionalFileAsync(
			solutionPath, "Extra.cs", "namespace SimpleLibrary; public class Extra { }", removeFromCompile: true);

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		await File.WriteAllTextAsync(additional, "namespace SimpleLibrary; public class Extra { /* changed */ }");
		await subject.OnFileChangedAsync(additional);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenACsFileThatIsBothCompiledAndAnAnalyzerAdditionalFileIsModified_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string additional = await AddAnalyzerAdditionalFileAsync(
			solutionPath, "Shared.cs", "namespace SimpleLibrary; public class Shared { }", removeFromCompile: false);

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		// The file is an additional document, so its change must reload (re-running the generators that read
		// it) rather than be folded in as compiled source, even though it is also a compiled document here.
		await File.WriteAllTextAsync(additional, "namespace SimpleLibrary; public class Shared { /* changed */ }");
		await subject.OnFileChangedAsync(additional);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenACsAnalyzerAdditionalFileIsDeleted_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string additional = await AddAnalyzerAdditionalFileAsync(
			solutionPath, "Extra.cs", "namespace SimpleLibrary; public class Extra { }", removeFromCompile: true);

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		File.Delete(additional);
		await subject.OnFileChangedAsync(additional);

		Assert.True(instance.IsDirty);
	}

	/// <summary>
	/// Writes a file into the single project and gives it the "C# analyzer additional file" build action by
	/// adding an <c>&lt;AdditionalFiles&gt;</c> item, optionally removing it from compilation (the canonical
	/// build-action change), and returns its full path.
	/// </summary>
	private static async Task<string> AddAnalyzerAdditionalFileAsync(string solutionPath, string fileName, string content, bool removeFromCompile)
	{
		string projectFile = FindFile(solutionPath, "*.csproj");
		string projectDir = Path.GetDirectoryName(projectFile)!;
		string filePath = Path.Combine(projectDir, fileName);
		await File.WriteAllTextAsync(filePath, content);

		string compileRemove = removeFromCompile ? $"    <Compile Remove=\"{fileName}\" />\n" : "";
		string itemGroup = $"  <ItemGroup>\n{compileRemove}    <AdditionalFiles Include=\"{fileName}\" />\n  </ItemGroup>\n";
		string edited = (await File.ReadAllTextAsync(projectFile)).Replace("</Project>", itemGroup + "</Project>");
		await File.WriteAllTextAsync(projectFile, edited);

		return filePath;
	}

	/// <summary>Appends a comment to the document via <see cref="ApplyPipeline"/>, the same route every
	/// mutating tool uses, so disk carries the atomic-writer artifacts and the snapshot is already advanced.</summary>
	private static async Task ApplyEditThroughPipelineAsync(RoslynInstance instance, string filePath, string appendedComment)
	{
		Solution solution = instance.CurrentSolution;
		Solution updated = solution;
		foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(filePath))
		{
			Document document = updated.GetDocument(id)!;
			string text = (await document.GetTextAsync()).ToString();
			updated = updated.WithDocumentText(id, SourceText.From(text + "\n" + appendedComment + "\n"));
		}

		await new ApplyPipeline().ApplyAsync(instance, updated);
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
