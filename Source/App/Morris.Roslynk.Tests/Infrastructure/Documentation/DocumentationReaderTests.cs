using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Documentation;

public class DocumentationReaderTests
{
	[Fact]
	public async Task WhenASymbolHasOwnDocs_ThenSectionsAndInlineTagsAreNormalized()
	{
		ISymbol symbol = await ResolveAsync("SimpleLibrary.Widget.Compute");

		SymbolDocumentation documentation = DocumentationReader.Read(symbol);

		Assert.Equal("own", documentation.Source);
		Assert.NotNull(documentation.Summary);
		Assert.Contains("`value`", documentation.Summary!);
		Assert.Contains("`Int32`", documentation.Summary!);
		Assert.Equal("Twice the input.", documentation.Returns);
		DocumentationParam parameter = Assert.Single(documentation.Params);
		Assert.Equal("value", parameter.Name);
	}

	[Fact]
	public async Task WhenAMemberUsesInheritDoc_ThenDocsComeFromTheInterface()
	{
		ISymbol symbol = await ResolveAsync("SimpleLibrary.Greeter.Greet");

		SymbolDocumentation documentation = DocumentationReader.Read(symbol);

		Assert.Equal("inherited", documentation.Source);
		Assert.NotNull(documentation.InheritedFrom);
		Assert.Equal("SimpleLibrary.IGreeter.Greet", documentation.InheritedFrom!.Symbol);
		Assert.EndsWith("IGreeter.cs", documentation.InheritedFrom.SourcePath);
		Assert.Contains("`name`", documentation.Summary!);
	}

	[Fact]
	public async Task WhenASymbolHasNoDocs_ThenSourceIsNone()
	{
		ISymbol symbol = await ResolveAsync("SimpleLibrary.Caller.Run");

		SymbolDocumentation documentation = DocumentationReader.Read(symbol);

		Assert.Equal("none", documentation.Source);
		Assert.Null(documentation.Summary);
	}

	private static async Task<ISymbol> ResolveAsync(string fullyQualifiedName)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		return (await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, fullyQualifiedName)).Single();
	}
}
