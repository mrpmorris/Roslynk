using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Razor;
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

	[Fact]
	public async Task WhenARazorFileIsEdited_ThenItsGeneratedDocumentIsRegeneratedWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string counter = FindFile(solutionPath, "Counter.razor");
		await File.WriteAllTextAsync(counter, (await File.ReadAllTextAsync(counter)) + "\n<span>regenerated-marker</span>\n");

		await subject.OnFileChangedAsync(counter);

		Assert.False(instance.IsDirty);
		Document generated = instance.CurrentSolution.Projects.SelectMany(p => p.Documents)
			.First(d => string.Equals(Path.GetFileName(d.FilePath), "Counter_razor.g.cs", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("regenerated-marker", (await generated.GetTextAsync()).ToString());
	}

	[Fact]
	public async Task WhenARazorFileIsRewrittenWithIdenticalContent_ThenNothingChanges()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		string counter = FindFile(solutionPath, "Counter.razor");
		await File.WriteAllBytesAsync(counter, await File.ReadAllBytesAsync(counter));

		await subject.OnFileChangedAsync(counter);

		Assert.False(instance.IsDirty);
		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenANewRazorFileAppears_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string added = Path.Combine(Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!, "Added.razor");
		await File.WriteAllTextAsync(added, "<p>added</p>");

		await subject.OnFileChangedAsync(added);

		// Membership is MSBuild's call: a new Razor source is not yet an additional document.
		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenACodeBehindParameterIsRenamed_ThenTheDependentPagesRazorIsRegeneratedWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchRazorMultiProjectSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string codeBehind = FindFile(solutionPath, "Widget.razor.cs");
		await File.WriteAllTextAsync(codeBehind, (await File.ReadAllTextAsync(codeBehind)).Replace("Count", "Total"));

		await subject.OnFileChangedAsync(codeBehind);

		// App's Page.razor still says Count="3". Generated before the rename, its code reads
		// nameof(Widget.Count) and fails with a phantom CS0117; regenerated, Count is just an attribute.
		Assert.False(instance.IsDirty);
		Project app = instance.CurrentSolution.Projects.Single(p => p.Name == "App");
		Compilation compilation = (await app.GetCompilationAsync())!;
		Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
	}

	[Fact]
	public async Task WhenACodeBehindEditOnlyChangesAMethodBody_ThenRazorIsNotRegenerated()
	{
		string solutionPath = TestSolutions.CreateScratchRazorMultiProjectSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		RazorGenerationState razor = instance.Workspace!.Razor;
		Dictionary<ProjectId, GeneratorDriver?> driversBefore = instance.CurrentSolution.ProjectIds.ToDictionary(id => id, id => razor.Get(id)?.Driver);

		string codeBehind = FindFile(solutionPath, "Widget.razor.cs");
		await File.WriteAllTextAsync(codeBehind, (await File.ReadAllTextAsync(codeBehind)).Replace("=> StateHasChanged();", "=> InvokeAsync(StateHasChanged);"));

		await subject.OnFileChangedAsync(codeBehind);

		Assert.False(instance.IsDirty);
		Assert.Contains("InvokeAsync", await ReadDocumentTextAsync(instance.CurrentSolution, codeBehind));
		Assert.All(driversBefore, pair => Assert.Same(pair.Value, razor.Get(pair.Key)?.Driver));
	}

	[Fact]
	public async Task WhenAFileInsideADotFolderOrNodeModulesChanges_ThenItIsIgnored()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string gitIndex = Path.Combine(projectDir, ".git", "index");
		string ideState = Path.Combine(projectDir, ".vs", "Simple", "v17", ".suo");
		string package = Path.Combine(projectDir, "node_modules", "left-pad", "index.js");
		foreach (string path in new[] { gitIndex, ideState, package })
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, "noise");
			await subject.OnFileChangedAsync(path);
		}

		Assert.False(instance.IsDirty);
		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenABuildFileInsideADotFolderChanges_ThenTheInstanceIsStillMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string imported = Path.Combine(projectDir, ".build", "Common.props");
		Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
		await File.WriteAllTextAsync(imported, "<Project />");

		await subject.OnFileChangedAsync(imported);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenANonRazorAdditionalFileChanges_ThenOnlyARealChangeIsFoldedInWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string additional = await AddAnalyzerAdditionalFileAsync(solutionPath, "settings.json", "{ \"a\": 1 }", removeFromCompile: false);

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		await File.WriteAllTextAsync(additional, "{ \"a\": 1 }");
		await subject.OnFileChangedAsync(additional);
		Assert.Same(before, instance.CurrentSolution);

		await File.WriteAllTextAsync(additional, "{ \"a\": 2 }");
		await subject.OnFileChangedAsync(additional);

		Assert.False(instance.IsDirty);
		TextDocument folded = instance.CurrentSolution.GetAdditionalDocument(instance.CurrentSolution.GetDocumentIdsWithFilePath(additional)[0])!;
		Assert.Equal("{ \"a\": 2 }", (await folded.GetTextAsync()).ToString());
	}

	[Fact]
	public async Task WhenASourceGeneratorsAdditionalFileChanges_ThenItsGeneratedCodeIsRegeneratedWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Assert.Contains("Hello from a csv", await GeneratedTextAsync(instance.CurrentSolution));

		string csv = FindFile(solutionPath, "Greeting.csv");
		await File.WriteAllTextAsync(csv, "Bonjour from a csv\n");
		await subject.OnFileChangedAsync(csv);

		Assert.False(instance.IsDirty);
		string generated = await GeneratedTextAsync(instance.CurrentSolution);
		Assert.Contains("Bonjour from a csv", generated);
		Assert.DoesNotContain("Hello from a csv", generated);

		static async Task<string> GeneratedTextAsync(Solution solution)
		{
			Project consumer = solution.Projects.Single(project => project.Name == "ConsumerLib");
			IEnumerable<SourceGeneratedDocument> generated = await consumer.GetSourceGeneratedDocumentsAsync();
			return string.Join("\n", await Task.WhenAll(generated.Select(async document => (await document.GetTextAsync()).ToString())));
		}
	}

	[Fact]
	public async Task WhenAFileThatCannotReachTheCompilerChanges_ThenItIsIgnored()
	{
		// Content, embedded resources and a tool's output written beside the sources (a weaver's .csv) are not
		// compile items, additional files or analyzer config, so Roslyn never sees them.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		foreach (string name in new[] { "appsettings.json", "SimpleLibrary.Weaver.csv", "Strings.resx" })
		{
			string path = Path.Combine(projectDir, name);
			await File.WriteAllTextAsync(path, "noise");
			await subject.OnFileChangedAsync(path);
			File.Delete(path);
			await subject.OnFileChangedAsync(path);
		}

		Assert.False(instance.IsDirty);
	}

	[Theory]
	[InlineData("Other.json")]
	[InlineData("Analyzers.globalconfig")]
	public async Task WhenANewFileAGlobCouldAddAppears_ThenTheInstanceIsMarkedDirty(string name)
	{
		// .json is the extension of an additional file already loaded, so a glob may well add this one too.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		_ = await AddAnalyzerAdditionalFileAsync(solutionPath, "settings.json", "{ }", removeFromCompile: false);
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string path = Path.Combine(Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!, name);
		await File.WriteAllTextAsync(path, "{ }");
		await subject.OnFileChangedAsync(path);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenAKnownAdditionalFileIsDeleted_ThenTheInstanceIsMarkedDirty()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string additional = await AddAnalyzerAdditionalFileAsync(solutionPath, "settings.json", "{ }", removeFromCompile: false);
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		File.Delete(additional);
		await subject.OnFileChangedAsync(additional);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenADirectoryHoldingLoadedDocumentsIsMovedAway_ThenTheInstanceIsMarkedDirty()
	{
		// Moving a directory out raises one event for its old path, none for the files it took along.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string folder = Path.Combine(projectDir, "Nested");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "Nested.cs"), "namespace Nested; public class Thing { }");
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		Directory.Move(folder, Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N")));
		await subject.OnFileChangedAsync(folder);

		Assert.True(instance.IsDirty);
	}

	[Fact]
	public async Task WhenASourceEventRacesTheServersOwnWrite_ThenTheFoldChangesNothing()
	{
		// The event is classified against the snapshot from before the write publishes; by the time its fold runs
		// the write has published the very same text, so replacing it would only fork every compilation.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string written = (await File.ReadAllTextAsync(greeter)) + "\n// written by the server\n";
		_ = await ReadDocumentTextAsync(instance.CurrentSolution, greeter); // loaded lazily from disk otherwise

		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task write = instance.EnqueueWriteAsync(async (current, token) =>
		{
			entered.TrySetResult();
			await release.Task;
			Solution updated = current;
			foreach (DocumentId id in current.GetDocumentIdsWithFilePath(greeter))
				updated = updated.WithDocumentText(id, SourceText.From(written));
			return new WriteResult(updated, [greeter]);
		});
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

		// Replaced whole, as the server's own writer does, so the live watcher never reads a half-written file.
		await File.WriteAllTextAsync(greeter + AtomicFileWriter.TempFileSuffix, written);
		File.Move(greeter + AtomicFileWriter.TempFileSuffix, greeter, overwrite: true);
		int enqueued = instance.EnqueuedWrites;

		Task fold = subject.OnFileChangedAsync(greeter);
		using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
		{
			while (instance.EnqueuedWrites == enqueued)
				await Task.Delay(10, timeout.Token);
		}
		release.TrySetResult();
		await write.WaitAsync(TimeSpan.FromSeconds(30));
		Solution afterWrite = instance.CurrentSolution;
		await fold.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Same(afterWrite, instance.CurrentSolution);
		Assert.False(instance.IsDirty);
	}

	[Fact]
	public async Task WhenADirectoryHoldingLoadedDocumentsChanges_ThenItIsIgnored()
	{
		// Writing a file raises a change event for its directory too; the file's own event covers the edit.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);
		Solution before = instance.CurrentSolution;

		string greeter = FindFile(solutionPath, "Greeter.cs");
		await subject.OnFileChangedAsync(Path.GetDirectoryName(greeter)!);

		Assert.False(instance.IsDirty);
		Assert.Same(before, instance.CurrentSolution);
	}

	[Fact]
	public async Task WhenADotFolderOrAFolderOutsideTheProjectsChanges_ThenItIsIgnored()
	{
		// The solution folder is watched shallowly for build files, so git's own activity reports .git there.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string solutionDir = Path.GetDirectoryName(solutionPath)!;
		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string[] directories = [Path.Combine(solutionDir, ".git"), Path.Combine(solutionDir, "docs"), Path.Combine(projectDir, ".vs")];
		foreach (string directory in directories)
		{
			Directory.CreateDirectory(directory);
			await subject.OnFileChangedAsync(directory);
		}

		Assert.False(instance.IsDirty);
	}

	[Fact]
	public async Task WhenANewDirectoryAppearsUnderAProject_ThenTheInstanceIsMarkedDirty()
	{
		// A directory moved or copied in raises one event for itself, none for the files inside it.
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new SolutionFileSync(instance);

		string projectDir = Path.GetDirectoryName(FindFile(solutionPath, "*.csproj"))!;
		string movedIn = Path.Combine(projectDir, "MovedIn");
		Directory.CreateDirectory(movedIn);
		await File.WriteAllTextAsync(Path.Combine(movedIn, "Extra.cs"), "namespace MovedIn; public class Extra { }");

		await subject.OnFileChangedAsync(movedIn);

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
