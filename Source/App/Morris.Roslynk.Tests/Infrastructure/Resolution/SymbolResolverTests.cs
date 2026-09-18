using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

public class SymbolResolverTests
{
	[Fact]
	public async Task WhenASignatureQualifiedNameIsGiven_ThenExactlyOneOverloadResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Ledger.Add(int)");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("SimpleLibrary.Ledger.Add(int)", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenABareNameIsGiven_ThenEveryOverloadResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Ledger.Add");

		Assert.Equal(2, matches.Count);
	}

	[Fact]
	public async Task WhenEmptyParenthesesAreGiven_ThenOnlyTheZeroArgOverloadResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Overloads.Pick()");

		ISymbol match = Assert.Single(matches);
		Assert.Empty(((IMethodSymbol)match).Parameters);
	}

	[Fact]
	public async Task WhenFullyQualifiedParameterTypesAreGiven_ThenTheSameOverloadResolves()
	{
		IReadOnlyList<ISymbol> minimal = await ResolveAsync("SimpleLibrary.Overloads.Pick(string, int)");
		IReadOnlyList<ISymbol> qualified = await ResolveAsync("SimpleLibrary.Overloads.Pick(System.String, System.Int32)");

		Assert.Single(minimal);
		Assert.Single(qualified);
		Assert.Equal(SymbolSignature.Of(minimal[0]), SymbolSignature.Of(qualified[0]));
	}

	[Fact]
	public async Task WhenANullableValueTypeIsGiven_ThenTheNonNullableOverloadIsNotMatched()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Overloads.Pick(int?)");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("SimpleLibrary.Overloads.Pick(int?)", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenARefModifierIsGiven_ThenTheByRefOverloadResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Overloads.Pick(ref int)");

		ISymbol match = Assert.Single(matches);
		Assert.Equal(RefKind.Ref, ((IMethodSymbol)match).Parameters[0].RefKind);
	}

	[Fact]
	public async Task WhenAGenericArityIsGiven_ThenTheGenericOverloadResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Overloads.Pick<T>(T)");

		ISymbol match = Assert.Single(matches);
		Assert.Equal(1, ((IMethodSymbol)match).Arity);
	}

	[Fact]
	public async Task WhenAnIndexerSignatureIsGiven_ThenThatIndexerResolves()
	{
		IReadOnlyList<ISymbol> matches = await ResolveAsync("SimpleLibrary.Overloads.this[string]");

		ISymbol match = Assert.Single(matches);
		Assert.True(((IPropertySymbol)match).IsIndexer);
		Assert.Equal("SimpleLibrary.Overloads.this[string]", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenAMetadataMemberSignatureIsGiven_ThenOnlyThatOverloadResolves()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		IReadOnlyList<ISymbol> matches = await new SymbolResolver()
			.FindByFullyQualifiedNameWithMetadataAsync(instance.CurrentSolution, "System.String.Substring(int)");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("System.String.Substring(int)", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenTheNameDoesNotMatch_ThenNothingResolves()
	{
		Assert.Empty(await ResolveAsync("SimpleLibrary.Ledger.Add(string)"));
		Assert.Empty(await ResolveAsync("SimpleLibrary.Ledger.Missing"));
	}

	private static async Task<IReadOnlyList<ISymbol>> ResolveAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		return await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, symbolName);
	}
}
