using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
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

		GetCodeActionsResponse response = await subject.GetCodeActions(solutionPath, greeter, unusedLine, 3, unusedLine, 20);

		Assert.Contains(response.Actions, action => action.DiagnosticId == "CS0219");
		Assert.All(response.Actions, action => Assert.False(string.IsNullOrEmpty(action.ActionId)));
	}

	[Fact]
	public async Task WhenTheDocumentIsUnknown_ThenAMessageIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		GetCodeActionsResponse response = await subject.GetCodeActions(TestSolutions.Simple, "NoSuchFile.cs", 1, 1);

		Assert.Empty(response.Actions);
		Assert.NotNull(response.Message);
	}
}
