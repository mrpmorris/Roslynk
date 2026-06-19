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

		string result = await subject.GetCodeActions(solutionPath, greeter, unusedLine, 3, unusedLine, 20);

		Assert.DoesNotContain("#error=", result);
		// A body line is "<actionId>,<kind>,CS0219 <title>"; the actionId is the first field and is non-empty.
		string fixLine = result.Split('\n').First(line => line.Contains(",CS0219 ", StringComparison.Ordinal));
		Assert.False(string.IsNullOrEmpty(fixLine.Split(',')[0]));
	}

	[Fact]
	public async Task WhenTheDocumentIsUnknown_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		string result = await subject.GetCodeActions(TestSolutions.Simple, "NoSuchFile.cs", 1, 1);

		Assert.Contains("#error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCodeActionsTool(registry, new CodeActionService());

		string result = await subject.GetCodeActions(TestSolutions.Simple, "Widget.cs", 1, 1);

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
