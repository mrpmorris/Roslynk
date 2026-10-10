using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

/// <summary>
/// A symbol declared only by a source generator is refused by rename_symbol: the generated declaration is
/// never written to disk and would be regenerated unchanged, so the rename could only rewrite references
/// and break the build. A symbol whose definition is hand-written (a [GeneratedRegex] partial method, a
/// partial type with a generated half) is still renamable — the generator follows the hand-written name.
/// </summary>
public class GeneratedOnlyRenameTests
{
	[Fact]
	public async Task WhenRenamingASymbolDeclaredOnlyByAGenerator_ThenNotSupportedAndNothingIsWritten()
	{
		// Greeting is reachable today (UsesGenerated.cs mentions it), so this also documents the latent
		// partial-rename being closed: before the guard the call sites were rewritten and the generator
		// re-emitted the old name.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		string usesGenerated = FindFile(solutionPath, "UsesGenerated.cs");
		string before = await File.ReadAllTextAsync(usesGenerated);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "GeneratedNamespace.Hello.Greeting", "Salutation");

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("GeneratorLib.HelloGenerator", result);
		Assert.DoesNotContain("applied=", result);
		Assert.Equal(before, await File.ReadAllTextAsync(usesGenerated));
	}

	[Fact]
	public async Task WhenRenamingAnUnmentionedGeneratedMember_ThenNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "ConsumerLib.Ctx.ReadValue2", "ReadThird", checkOnly: true);

		Assert.Contains("error=NotSupported", result);
		Assert.DoesNotContain("applied=", result);
	}

	[Fact]
	public async Task WhenARenegexPartialMethodsDefinitionIsHandWritten_ThenTheRenameIsAllowed()
	{
		// The generated implementation part of Letters follows the hand-written definition, so the rename is
		// safe: the generator re-emits it under the new name on the next compilation.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "ConsumerLib.Patterns.Letters", "Vowels", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Patterns.cs", result);
	}

	[Fact]
	public async Task WhenAPartialTypeHasAGeneratedHalf_ThenTheRenameIsAllowed()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "ConsumerLib.Ctx", "Client", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Ctx.cs", result);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
