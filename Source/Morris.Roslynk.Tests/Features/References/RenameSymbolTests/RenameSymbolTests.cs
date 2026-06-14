using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
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
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ApplyPipeline());

		RenameSymbolResult result = await subject.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

		Assert.True(result.IsSuccess);
		Assert.True(result.Applied);
		Assert.NotEmpty(result.ChangedFiles!);

		string greeter = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Greeter.cs"));
		Assert.Contains("class Welcomer", greeter);

		string caller = await File.ReadAllTextAsync(Path.Combine(libraryDir, "Caller.cs"));
		Assert.Contains("new Welcomer()", caller);
	}

	[Fact]
	public async Task WhenTheNewNameIsNotAValidIdentifier_ThenItIsRefusedAndNothingIsWritten()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ApplyPipeline());

		RenameSymbolResult result = await subject.RenameSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter", "1nvalid");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Invalid, result.Error!.Code);
		Assert.False(result.Applied);
		Assert.Null(result.ChangedFiles);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ApplyPipeline());

		RenameSymbolResult result = await subject.RenameSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter", "Welcomer");

		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
