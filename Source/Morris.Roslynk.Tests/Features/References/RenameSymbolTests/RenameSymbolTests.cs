using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

public class RenameSymbolTests
{
	[Fact]
	public async Task WhenRenamingAType_ThenItsDeclarationAndReferencesAreRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string libraryDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");

		using var registry = new InstanceRegistry();
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ApplyPipeline());

		RenameSymbolResponse response = await subject.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

		Assert.True(response.Applied);
		Assert.NotEmpty(response.ChangedFiles);

		string greeter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Greeter.cs"));
		Assert.Contains("class Welcomer", greeter);

		string caller = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Caller.cs"));
		Assert.Contains("new Welcomer()", caller);
	}

	[Fact]
	public async Task WhenTheNewNameIsNotAValidIdentifier_ThenItIsRefusedAndNothingIsWritten()
	{
		using var registry = new InstanceRegistry();
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ApplyPipeline());

		RenameSymbolResponse response = await subject.RenameSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter", "1nvalid");

		Assert.False(response.Applied);
		Assert.NotNull(response.Message);
		Assert.Empty(response.ChangedFiles);
	}
}
