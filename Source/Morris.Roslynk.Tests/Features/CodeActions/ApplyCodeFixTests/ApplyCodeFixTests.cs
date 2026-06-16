using Morris.Roslynk.Features.CodeActions.ApplyCodeFix;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.CodeActions.ApplyCodeFixTests;

public class ApplyCodeFixTests
{
	[Fact]
	public async Task WhenFixingADiagnosticById_ThenTheFileIsUpdated()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyCodeFixTool(registry, new CodeActionService(), new ApplyPipeline());

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219");

		Assert.Contains("#applied=true", result);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenNoSuchDiagnosticExists_ThenNotFoundIsReturned()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyCodeFixTool(registry, new CodeActionService(), new ApplyPipeline());

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS9999");

		Assert.Contains("#error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ApplyCodeFixTool(registry, new CodeActionService(), new ApplyPipeline());

		string result = await subject.ApplyCodeFix(TestSolutions.Simple, "Widget.cs", "CS0219");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
