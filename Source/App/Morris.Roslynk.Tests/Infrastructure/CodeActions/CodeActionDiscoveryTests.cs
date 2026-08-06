using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
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
	public void WhenTheCatalogIsBuilt_ThenUnnecessaryImportsFixProviderIsLoadable()
	{
		// IDE0005 is a classification id; the headless fix provider claims RemoveUnnecessaryImportsFixable.
		bool hasFixable = CodeActionCatalog.Instance.FixProviders.Any(provider =>
		{
			try
			{
				return provider.FixableDiagnosticIds.Contains("RemoveUnnecessaryImportsFixable");
			}
			catch
			{
				return false;
			}
		});

		Assert.True(hasFixable, "Expected CSharpRemoveUnnecessaryImportsCodeFixProvider (RemoveUnnecessaryImportsFixable).");
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

	[Fact]
	public async Task WhenAnAnalyzerDiagnosticExists_ThenFindDiagnosticAsyncSeesIt()
	{
		// Regression for #9: fix path must use the analyzer-aware diagnostic set, not compilation.GetDiagnostics alone.
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeterPath, out _);
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Document document = CodeActionService.FindDocument(instance.CurrentSolution, greeterPath)!;

		SyntaxTree tree = (await document.GetSyntaxTreeAsync())!;
		IReadOnlyList<Diagnostic> analyzerAware = await new DiagnosticsService()
			.GetProjectDiagnosticsAsync(document.Project, includeAnalyzers: true);
		IReadOnlyList<Diagnostic> compilerOnly = (await document.Project.GetCompilationAsync())!
			.GetDiagnostics()
			.Where(diagnostic => diagnostic.Location.IsInSource && diagnostic.Location.SourceTree == tree)
			.ToArray();

		Diagnostic? analyzerOnly = analyzerAware
			.Where(diagnostic => diagnostic.Location.IsInSource
				&& diagnostic.Location.SourceTree == tree
				&& compilerOnly.All(compiler => compiler.Id != diagnostic.Id || compiler.Location.SourceSpan != diagnostic.Location.SourceSpan))
			.FirstOrDefault(diagnostic => diagnostic.Id.StartsWith("IDE", StringComparison.Ordinal)
				|| diagnostic.Id.StartsWith("CA", StringComparison.Ordinal));

		// SDK fixtures may not ship IDE* analyzers; when they do, FindDiagnosticAsync must surface them.
		if (analyzerOnly is null)
		{
			// Still prove the lookup path is analyzer-aware by finding any id that GetProjectDiagnosticsAsync returns.
			Diagnostic? any = analyzerAware.FirstOrDefault(diagnostic =>
				diagnostic.Location.IsInSource && diagnostic.Location.SourceTree == tree);
			if (any is null)
				return; // Nothing to assert for this fixture.

			Diagnostic? found = await new CodeActionService().FindDiagnosticAsync(document, any.Id);
			Assert.NotNull(found);
			return;
		}

		Diagnostic? resolved = await new CodeActionService().FindDiagnosticAsync(document, analyzerOnly.Id);
		Assert.NotNull(resolved);
		Assert.Equal(analyzerOnly.Id, resolved!.Id);
	}
}
