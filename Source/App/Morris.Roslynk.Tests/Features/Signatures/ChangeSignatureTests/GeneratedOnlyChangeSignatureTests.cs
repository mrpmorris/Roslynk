using Morris.Roslynk.Features.Signatures.ChangeSignature;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.Signatures.ChangeSignatureTests;

/// <summary>
/// A method declared only by a source generator is refused with an explicit error instead of faulting on
/// its generated document (or silently no-oping): the declaration is never persisted and the generator
/// would re-emit the old signature.
/// </summary>
public class GeneratedOnlyChangeSignatureTests
{
	[Fact]
	public async Task WhenChangingTheSignatureOfAGeneratedMethod_ThenNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			solutionPath, "ConsumerLib.Ctx.ReadValue2", "int", "factor", "1", checkOnly: true);

		Assert.Contains("error=NotSupported", result);
		Assert.DoesNotContain("error=Faulted", result);
		Assert.DoesNotContain("applied=", result);
	}
}
