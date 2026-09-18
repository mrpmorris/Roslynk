using Morris.Roslynk.Infrastructure.CodeActions;

namespace Morris.Roslynk.Tests.Infrastructure.CodeActions;

public class CodeActionCatalogTests
{
	[Fact]
	public void WhenTheCatalogIsBuilt_ThenCompilerIdsAreFixable()
	{
		// The catalog is built by reflection over the Features assemblies, so a packaging change could
		// silently empty it and leave every diagnostic unfixable.
		Assert.Contains("CS0219", CodeActionCatalog.Instance.FixableDiagnosticIds);
	}

	[Fact]
	public void WhenTheCatalogIsBuilt_ThenTheUnnecessaryImportsTriggerIsFixable()
	{
		// Roslyn's fixer registers against this private trigger rather than IDE0005; the analyzer must be
		// kept by the narrowing pass on the strength of it.
		Assert.Contains("RemoveUnnecessaryImportsFixable", CodeActionCatalog.Instance.FixableDiagnosticIds);
	}

	[Fact]
	public void WhenAFixIsTriggeredByAPrivateId_ThenItIsReportedUnderThePublicId()
	{
		Assert.Equal("IDE0005", CodeActionCatalog.PublicId("RemoveUnnecessaryImportsFixable"));
	}

	[Fact]
	public void WhenAFixIsTriggeredByItsOwnId_ThenThatIdIsReported()
	{
		Assert.Equal("CS0219", CodeActionCatalog.PublicId("CS0219"));
	}
}
