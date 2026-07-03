using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.CodeActions.ApplyCodeActionTests;

public class ApplyCodeActionTests
{
	[Fact]
	public async Task WhenApplyingADiscoveredFix_ThenTheFileIsUpdated()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var service = new CodeActionService();
		string actionId = await DiscoverRemoveUnusedAsync(registry, service, solutionPath, greeter, unusedLine);
		var subject = new ApplyCodeActionTool(registry, service, new ApplyPipeline());

		string result = await subject.ApplyCodeAction(solutionPath, actionId);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenApplyingWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var service = new CodeActionService();
		string before = await File.ReadAllTextAsync(greeter);
		string actionId = await DiscoverRemoveUnusedAsync(registry, service, solutionPath, greeter, unusedLine);
		var subject = new ApplyCodeActionTool(registry, service, new ApplyPipeline());

		string result = await subject.ApplyCodeAction(solutionPath, actionId, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheActionIdIsInvalid_ThenItIsRefused()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ApplyCodeActionTool(registry, new CodeActionService(), new ApplyPipeline());

		string result = await subject.ApplyCodeAction(TestSolutions.Simple, "not-a-valid-id");

		Assert.Contains("error=Invalid", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ApplyCodeActionTool(registry, new CodeActionService(), new ApplyPipeline());

		string result = await subject.ApplyCodeAction(TestSolutions.Simple, "anything");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static async Task<string> DiscoverRemoveUnusedAsync(InstanceRegistry registry, CodeActionService service, string solutionPath, string greeter, int unusedLine)
	{
		var getActions = new GetCodeActionsTool(registry, service);
		string actions = await getActions.GetCodeActions(solutionPath, greeter, unusedLine, 3, unusedLine, 20);

		// A body line is "<actionId>,<kind>,CS0219 <title>"; take the first field of the CS0219 line.
		string fixLine = actions.Split('\n').First(line => line.Contains(",CS0219 ", StringComparison.Ordinal));
		return fixLine.Split(',')[0];
	}
}
