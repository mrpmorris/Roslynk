using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.SourceGeneratedDeclarationsTests;

public class SourceGeneratedDeclarationsTests
{
	[Fact]
	public async Task WhenAProjectHasGeneratedDocuments_ThenGeneratedOnlyDeclarationsAreFound()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);
		Project consumer = instance.CurrentSolution.Projects.Single(project => project.Name == "ConsumerLib");

		IReadOnlyList<ISymbol> found = await SourceGeneratedDeclarations.FindAsync(consumer, name => name == "Unlisted");

		ISymbol symbol = Assert.Single(found);
		Assert.Equal("Unlisted", symbol.Name);
		Assert.All(symbol.DeclaringSyntaxReferences, reference => Assert.Contains("Shape.g.cs", reference.SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredInARegularAndAGeneratedDocument_ThenTheGeneratedPassReturnsTheMergedSymbol()
	{
		// Shape is a partial type with a hand-written and a generated part: one merged symbol with a
		// declaring reference in the generated document, so it is returned here (and the tool's identity
		// index collapses the copy the declaration search already returned).
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);
		Project consumer = instance.CurrentSolution.Projects.Single(project => project.Name == "ConsumerLib");

		IReadOnlyList<ISymbol> found = await SourceGeneratedDeclarations.FindAsync(consumer, name => name == "Shape");

		ISymbol symbol = Assert.Single(found);
		Assert.Equal("Shape", symbol.Name);
		Assert.Contains(symbol.DeclaringSyntaxReferences.Select(reference => reference.SyntaxTree.FilePath), path => path.Contains("Shape.g.cs", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredOnlyInARegularDocument_ThenTheGeneratedPassSkipsIt()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);
		Project consumer = instance.CurrentSolution.Projects.Single(project => project.Name == "ConsumerLib");

		IReadOnlyList<ISymbol> found = await SourceGeneratedDeclarations.FindAsync(consumer, name => name == "UsesGenerated");

		Assert.Empty(found);
	}

	[Fact]
	public async Task WhenAProjectHasNoGenerators_ThenNothingIsReturned()
	{
		// GeneratorLib has no analyzer references of its own; the probe must not build its compilation at all.
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);
		Project generatorLib = instance.CurrentSolution.Projects.Single(project => project.Name == "GeneratorLib");

		IReadOnlyList<ISymbol> found = await SourceGeneratedDeclarations.FindAsync(generatorLib, name => name.Contains('e'));

		Assert.Empty(found);
	}
}
