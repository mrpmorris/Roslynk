using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;
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

		ApplyCodeActionResult result = await subject.ApplyCodeAction(solutionPath, actionId);

		Assert.True(result.IsSuccess);
		Assert.True(result.Applied);
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

		ApplyCodeActionResult result = await subject.ApplyCodeAction(solutionPath, actionId, checkOnly: true);

		Assert.True(result.IsSuccess);
		Assert.False(result.Applied);
		Assert.NotEmpty(result.ChangedFiles!);
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheActionIdIsInvalid_ThenItIsRefused()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ApplyCodeActionTool(registry, new CodeActionService(), new ApplyPipeline());

		ApplyCodeActionResult result = await subject.ApplyCodeAction(TestSolutions.Simple, "not-a-valid-id");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Invalid, result.Error!.Code);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ApplyCodeActionTool(registry, new CodeActionService(), new ApplyPipeline());

		ApplyCodeActionResult result = await subject.ApplyCodeAction(TestSolutions.Simple, "anything");

		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static async Task<string> DiscoverRemoveUnusedAsync(InstanceRegistry registry, CodeActionService service, string solutionPath, string greeter, int unusedLine)
	{
		var getActions = new GetCodeActionsTool(registry, service);
		GetCodeActionsResult actions = await getActions.GetCodeActions(solutionPath, greeter, unusedLine, 3, unusedLine, 20);
		return actions.Actions!.First(action => action.DiagnosticId == "CS0219").ActionId;
	}
}
