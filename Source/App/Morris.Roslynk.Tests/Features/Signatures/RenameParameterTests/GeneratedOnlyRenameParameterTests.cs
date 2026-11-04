using Morris.Roslynk.Features.Signatures.RenameParameter;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Signatures.RenameParameterTests;

/// <summary>
/// A parameter of a generator-declared method is refused, and a related member declared only by a
/// generator is left in unchangedRelated rather than half-renamed (its declaration would never be
/// persisted). A hand-written partial method is still renamed across its parts.
/// </summary>
public class GeneratedOnlyRenameParameterTests : MutatingTestBase
{
	[Fact]
	public async Task WhenRenamingAParameterOfAGeneratedMethod_ThenNotSupported()
	{
		string solutionPath = TestSolutions.GeneratorWithBuiltGenerator;
		InstanceRegistry registry = await TestSolutions.SharedAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, "ConsumerLib.Ctx.Cache.TryResolve", "key", "name", checkOnly: true);

		Assert.Contains("error=NotSupported", result);
		Assert.DoesNotContain("applied=", result);
	}

	[Fact]
	public async Task WhenTheMethodIsAHandWrittenPartialMethod_ThenBothPartsAreRenamed()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchGeneratorSolution());
		var subject = new RenameParameterTool(Registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, "ConsumerLib.Widget.Compute", "value", "amount", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Widget.Declaration.cs", result);
		Assert.Contains("Widget.Implementation.cs", result);
	}
}
