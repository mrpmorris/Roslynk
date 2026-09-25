using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Infrastructure.Writing;

public class ApplyPipelineTests
{
	[Fact]
	public async Task WhenARazorFileOnDiskDiffersFromTheLoadedText_ThenTheWriteIsRefusedAsStale()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Solution solution = instance.CurrentSolution;

		DocumentId additionalId = solution.GetDocumentIdsWithFilePath(counterPath)
			.First(id => solution.GetAdditionalDocument(id) is not null);
		SourceText loaded = await solution.GetAdditionalDocument(additionalId)!.GetTextAsync();
		Solution updated = solution.WithAdditionalDocumentText(
			additionalId, SourceText.From(loaded.ToString().Replace("CurrentCount", "Total")));

		// An external edit lands on disk after the solution was loaded; the stale guard must refuse the write.
		string externallyEdited = loaded.ToString() + "\n@* external edit *@\n";
		await File.WriteAllTextAsync(counterPath, externallyEdited);

		await Assert.ThrowsAsync<StaleWriteException>(() => new ApplyPipeline().ApplyAsync(instance, updated));
		Assert.Equal(externallyEdited, await File.ReadAllTextAsync(counterPath));
	}

	[Fact]
	public async Task WhenAnAdditionalDocumentChanges_ThenItsPathIsListedForCheckOnly()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Solution solution = instance.CurrentSolution;

		DocumentId additionalId = solution.GetDocumentIdsWithFilePath(counterPath)
			.First(id => solution.GetAdditionalDocument(id) is not null);
		SourceText loaded = await solution.GetAdditionalDocument(additionalId)!.GetTextAsync();
		Solution updated = solution.WithAdditionalDocumentText(
			additionalId, SourceText.From(loaded.ToString().Replace("CurrentCount", "Total")));

		IReadOnlyList<string> changed = ApplyPipeline.GetChangedFilePaths(solution, updated);

		Assert.Contains(counterPath, changed);
	}
	[Fact]
	public async Task WhenAChangedDocumentWasEditedAfterTheBaseSnapshot_ThenTheWriteIsRefusedAsStale()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string greeterPath = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Solution basedOn = instance.CurrentSolution;
		DocumentId greeterId = basedOn.GetDocumentIdsWithFilePath(greeterPath).First();
		string loaded = (await basedOn.GetDocument(greeterId)!.GetTextAsync()).ToString();
		Solution updated = basedOn.WithDocumentText(greeterId, SourceText.From(loaded + "// computed edit\n"));

		// Another write edits the same file (disk and model agree) after the update was computed.
		string intervening = loaded + "// intervening edit\n";
		await new ApplyPipeline().ApplyAsync(instance, basedOn.WithDocumentText(greeterId, SourceText.From(intervening)));

		await Assert.ThrowsAsync<StaleWriteException>(() => new ApplyPipeline().ApplyAsync(instance, updated, basedOn));
		Assert.Equal(intervening, await File.ReadAllTextAsync(greeterPath));
	}

	[Fact]
	public async Task WhenOnlyAnUnrelatedDocumentChangedAfterTheBaseSnapshot_ThenTheWriteSucceeds()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string directory = Path.GetDirectoryName(solutionPath)!;
		string greeterPath = Directory.EnumerateFiles(directory, "Greeter.cs", SearchOption.AllDirectories).First();
		string widgetPath = Directory.EnumerateFiles(directory, "Widget.cs", SearchOption.AllDirectories).First();

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Solution basedOn = instance.CurrentSolution;
		DocumentId greeterId = basedOn.GetDocumentIdsWithFilePath(greeterPath).First();
		DocumentId widgetId = basedOn.GetDocumentIdsWithFilePath(widgetPath).First();
		string greeter = (await basedOn.GetDocument(greeterId)!.GetTextAsync()).ToString();
		string widget = (await basedOn.GetDocument(widgetId)!.GetTextAsync()).ToString();
		Solution updated = basedOn.WithDocumentText(greeterId, SourceText.From(greeter + "// computed edit\n"));

		await new ApplyPipeline().ApplyAsync(instance, basedOn.WithDocumentText(widgetId, SourceText.From(widget + "// unrelated\n")));
		await new ApplyPipeline().ApplyAsync(instance, updated, basedOn);

		Assert.Equal(greeter + "// computed edit\n", await File.ReadAllTextAsync(greeterPath));

		// The intervening edit to the other file is neither reverted on disk nor in the published model.
		Assert.Equal(widget + "// unrelated\n", await File.ReadAllTextAsync(widgetPath));
		Assert.Equal(widget + "// unrelated\n", (await instance.CurrentSolution.GetDocument(widgetId)!.GetTextAsync()).ToString());
	}
}
