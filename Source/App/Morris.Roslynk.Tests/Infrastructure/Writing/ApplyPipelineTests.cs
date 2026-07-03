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
}
