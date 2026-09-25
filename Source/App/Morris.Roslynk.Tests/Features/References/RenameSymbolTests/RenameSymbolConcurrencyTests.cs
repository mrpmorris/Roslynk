using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

public class RenameSymbolConcurrencyTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

	[Fact]
	public async Task WhenAnUnrelatedFileIsEditedAfterTheRenameIsComputed_ThenTheRenameSucceedsAndTheEditSurvives()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		string ledgerPath = Path.Combine(libraryDir, "Ledger.cs");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		string edited = await File.ReadAllTextAsync(ledgerPath) + "// intervening edit\n";

		string result = await RenameWithInterveningEditAsync(registry, instance, solutionPath, ledgerPath, edited);

		Assert.Contains("applied=Y", result);
		Assert.Contains("class Welcomer", await File.ReadAllTextAsync(Path.Combine(libraryDir, "Greeter.cs")));
		Assert.Equal(edited, await File.ReadAllTextAsync(ledgerPath));
		Document ledger = instance.CurrentSolution.GetDocument(instance.CurrentSolution.GetDocumentIdsWithFilePath(ledgerPath).First())!;
		Assert.Equal(edited, (await ledger.GetTextAsync()).ToString());
	}

	[Fact]
	public async Task WhenARenamedFileIsEditedAfterTheRenameIsComputed_ThenStaleIsReturnedAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		string callerPath = Path.Combine(libraryDir, "Caller.cs");
		string greeterPath = Path.Combine(libraryDir, "Greeter.cs");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		string greeterBefore = await File.ReadAllTextAsync(greeterPath);
		string edited = await File.ReadAllTextAsync(callerPath) + "// intervening edit\n";

		string result = await RenameWithInterveningEditAsync(registry, instance, solutionPath, callerPath, edited);

		Assert.Contains("error=Stale", result);
		Assert.Contains("stale=", result);
		Assert.Equal(edited, await File.ReadAllTextAsync(callerPath));
		Assert.Equal(greeterBefore, await File.ReadAllTextAsync(greeterPath));
	}

	[Fact]
	public async Task WhenARenamedFileChangedOnDiskSinceLoad_ThenStaleIsReturnedWithThePath()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		string callerPath = Path.Combine(libraryDir, "Caller.cs");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string edited = await File.ReadAllTextAsync(callerPath) + "// external edit\n";
		await File.WriteAllTextAsync(callerPath, edited);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

		// The watcher may fold the edit first (then the rename succeeds on the new text); otherwise the disk guard refuses it.
		if (result.Contains("applied=Y"))
		{
			Assert.Contains("new Welcomer()", await File.ReadAllTextAsync(callerPath));
			Assert.Contains("// external edit", await File.ReadAllTextAsync(callerPath));
		}
		else
		{
			Assert.Contains("error=Stale", result);
			Assert.DoesNotContain("error=Faulted", result);
			Assert.Contains("stale=", result);
			Assert.Equal(edited, await File.ReadAllTextAsync(callerPath));
		}
	}

	/// <summary>
	/// Holds the write queue with a blocking write, lets the rename compute and queue behind it, then has the
	/// blocking write publish <paramref name="editedText"/> for <paramref name="path"/> (on disk and in the model,
	/// as a watcher fold would) before the rename's write runs.
	/// </summary>
	private static async Task<string> RenameWithInterveningEditAsync(InstanceRegistry registry, RoslynInstance instance, string solutionPath, string path, string editedText)
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<IReadOnlyList<string>> blocker = instance.EnqueueWriteAsync(async (solution, _) =>
		{
			started.SetResult();
			await release.Task;
			await File.WriteAllTextAsync(path, editedText);
			Solution updated = solution;
			foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(path))
				updated = updated.WithDocumentText(id, SourceText.From(editedText));
			return new WriteResult(updated, []);
		});
		await started.Task.WaitAsync(Timeout);
		int enqueued = instance.EnqueuedWrites;

		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());
		Task<string> rename = subject.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

		using (var waiting = new CancellationTokenSource(Timeout))
		{
			while (instance.EnqueuedWrites == enqueued && !rename.IsCompleted)
				await Task.Delay(10, waiting.Token);
		}

		release.SetResult();
		await blocker.WaitAsync(Timeout);
		return await rename.WaitAsync(Timeout);
	}
}
