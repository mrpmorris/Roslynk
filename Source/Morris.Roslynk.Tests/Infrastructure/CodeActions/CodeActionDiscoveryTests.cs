using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Infrastructure.CodeActions;

public class CodeActionDiscoveryTests
{
	[Fact]
	public void WhenTheCatalogIsBuilt_ThenItDiscoversCSharpProviders()
	{
		CodeActionCatalog catalog = CodeActionCatalog.Instance;

		Assert.True(catalog.FixProviders.Count > 0, "Expected at least one fix provider.");
		Assert.True(catalog.RefactoringProviders.Count > 0, "Expected at least one refactoring provider.");
	}

	[Fact]
	public async Task WhenDiscoveringOnAnUnusedLocal_ThenARemoveFixIsFound()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var service = new CodeActionService();

		Document document = CodeActionService.FindDocument(instance.CurrentSolution, greeter)!;
		SourceText text = await document.GetTextAsync();
		TextSpan span = CodeActionService.SpanFor(text, unusedLine, 1, unusedLine, 20);

		IReadOnlyList<DiscoveredAction> actions = await service.DiscoverAsync(document, span);

		Assert.Contains(actions, action => action.DiagnosticId == "CS0219");
	}
}
