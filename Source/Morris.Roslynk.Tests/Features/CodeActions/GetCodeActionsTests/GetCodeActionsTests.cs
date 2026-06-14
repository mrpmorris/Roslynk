using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.CodeActions.GetCodeActionsTests;

public class GetCodeActionsTests
{
	[Fact]
	public async Task WhenAFixableDiagnosticIsAtThePosition_ThenItsActionIsListed()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		GetCodeActionsResult result = await subject.GetCodeActions(solutionPath, greeter, unusedLine, 3, unusedLine, 20);

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Actions!, action => action.DiagnosticId == "CS0219");
		Assert.All(result.Actions!, action => Assert.False(string.IsNullOrEmpty(action.ActionId)));
	}

	[Fact]
	public async Task WhenTheDocumentIsUnknown_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		GetCodeActionsResult result = await subject.GetCodeActions(TestSolutions.Simple, "NoSuchFile.cs", 1, 1);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		GetCodeActionsResult result = await subject.GetCodeActions(TestSolutions.Simple, "Widget.cs", 1, 1);

		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
