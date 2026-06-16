using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

public class EnclosingDeclarationTests
{
	[Fact]
	public async Task WhenAReferenceSitsInsideAMethod_ThenThePathEndsAtTheMethodNestedInItsType()
	{
		EnclosingPath path = await ResolveFirstReferenceAsync("SimpleLibrary.Greeter");

		Assert.Equal("SimpleLibrary", path.Namespace);

		EnclosingSegment leaf = path.Segments[^1];
		Assert.Equal("method", leaf.Kind);
		Assert.Equal("Run", leaf.Name);
		Assert.Contains(path.Segments, segment => segment.Kind == "class" && segment.Name == "Caller");
	}

	[Fact]
	public async Task WhenAReferenceSitsAtTypeLevel_ThenThePathEndsAtTheType()
	{
		EnclosingPath path = await ResolveFirstReferenceAsync("SimpleLibrary.IGreeter");

		Assert.Equal("SimpleLibrary", path.Namespace);

		EnclosingSegment leaf = path.Segments[^1];
		Assert.Equal("class", leaf.Kind);
		Assert.Equal("Greeter", leaf.Name);
	}

	private static async Task<EnclosingPath> ResolveFirstReferenceAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		Solution solution = instance.CurrentSolution;

		ISymbol symbol = (await new SymbolResolver().FindByFullyQualifiedNameAsync(solution, symbolName))[0];
		Location location = (await SymbolFinder.FindReferencesAsync(symbol, solution))
			.SelectMany(referenced => referenced.Locations)
			.First(reference => reference.Location.IsInSource)
			.Location;

		return await EnclosingDeclaration.ResolveAsync(solution, location);
	}
}
