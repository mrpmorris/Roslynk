using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

/// <summary>
/// Renaming symbols declared in .razor files: the Renamer's edits land in the Razor-generated .g.cs
/// documents, are mapped back through the #line directives, and are written to the .razor sources.
/// </summary>
public class RazorRenameTests
{
	private static RenameSymbolTool CreateSubject(InstanceRegistry registry) =>
		new(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

	[Fact]
	public async Task WhenRenamingAFieldDeclaredInARazorCodeBlock_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total");

		Assert.Contains("applied=Y", result);
		Assert.Contains("resolvedSymbol=RazorLib.Counter.CurrentCount", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("private int Total;", counter);
		Assert.Contains("Count: @Total", counter);
		Assert.Contains("Total = StartAt;", counter);
		Assert.Contains("Total++;", counter);
		Assert.DoesNotContain("CurrentCount", counter);
	}

	[Fact]
	public async Task WhenRenamingAMethodWiredOnlyInMarkup_ThenTheMarkupAttributeIsRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.IncrementCount", "Bump");

		Assert.Contains("applied=Y", result);

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("@onclick=\"Bump\"", counter);
		Assert.Contains("private void Bump()", counter);
		Assert.DoesNotContain("IncrementCount", counter);
	}

	[Fact]
	public async Task WhenCheckOnlyIsPassed_ThenTheRazorFileIsListedAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Counter.razor");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");

		string counter = await File.ReadAllTextAsync(counterPath);
		Assert.Contains("CurrentCount", counter);
		Assert.DoesNotContain("Total", counter);
	}

	[Fact]
	public async Task WhenRenamingAComponentParameter_ThenAttributeUsagesInOtherComponentsAreRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool subject = CreateSubject(registry);

		string result = await subject.RenameSymbol(solutionPath, "RazorLib.Counter.StartAt", "StartFrom");

		Assert.Contains("applied=Y", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "UsesCounter.razor");

		string counter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Counter.razor"));
		Assert.Contains("public int StartFrom { get; set; }", counter);
		Assert.Contains("Starting from @StartFrom", counter);
		Assert.DoesNotContain("StartAt", counter);

		// The generator emits the attribute name as nameof(...) inside a #line-mapped region, so the
		// markup usage in the consuming component is renamed too.
		string usesCounter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "UsesCounter.razor"));
		Assert.Contains("<Counter StartFrom=\"5\" />", usesCounter);
	}

	[Fact]
	public async Task WhenARazorRenameIsApplied_ThenSubsequentReadsSeeTheNewName()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		RenameSymbolTool rename = CreateSubject(registry);

		string renameResult = await rename.RenameSymbol(solutionPath, "RazorLib.Counter.CurrentCount", "Total");
		Assert.Contains("applied=Y", renameResult);

		var findReferences = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());
		string referencesResult = await findReferences.FindReferences(solutionPath, "RazorLib.Counter.Total");

		Assert.Contains("resolvedSymbol=RazorLib.Counter.Total", referencesResult);
		Assert.Contains(referencesResult.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
	}
}
