using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
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

		ApplyCodeActionResponse response = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219");

		Assert.True(response.Applied);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenNoSuchDiagnosticExists_ThenItIsRefused()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyCodeFixTool(registry, new CodeActionService(), new ApplyPipeline());

		ApplyCodeActionResponse response = await subject.ApplyCodeFix(solutionPath, greeter, "CS9999");

		Assert.False(response.Applied);
		Assert.NotNull(response.Message);
	}
}
