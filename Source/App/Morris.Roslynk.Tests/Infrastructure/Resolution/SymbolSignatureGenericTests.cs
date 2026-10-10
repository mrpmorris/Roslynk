using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// The relaxed matching rules for generic names: a bare segment matches any declared arity, a written one
/// constrains it, exact matches always win, and reflection metadata spellings parse like C# spellings.
/// </summary>
public class SymbolSignatureGenericTests
{
	private const string Source =
		"""
		namespace Repro
		{
			public class Box<T>
			{
				public Box() { }

				public T? Get() => default;
				public T? this[int index] => default;
			}

			public class Pair { }
			public class Pair<T> { }

			public class Multi<T> { }
			public class Multi<T1, T2> { }

			public class Outer<T>
			{
				public class Inner<U>
				{
					public void Run() { }
					public void Parse()
					{
						void Tokenize() { }
					}
				}
			}
		}
		""";

	[Theory]
	[InlineData("Repro.Box`1", "Box", 1)]
	[InlineData("Repro.Outer`1.Inner`1", "Inner", 1)]
	[InlineData("Repro.Outer`1+Inner", "Outer`1+Inner", -1)]
	public void WhenABacktickArityIsWritten_ThenItParsesOffTheSimpleName(string name, string expectedSimpleName, int expectedArity)
	{
		Assert.True(SymbolSignature.TryParse(name, out SymbolSignatureQuery query));

		Assert.Equal(expectedSimpleName, query.SimpleName);
		Assert.Equal(expectedArity, query.Arity);
	}

	[Theory]
	[InlineData("Repro.Box1")]
	[InlineData("Repro.Box`")]
	[InlineData("Repro.Box`x")]
	public void WhenABacktickSuffixIsNotAnArity_ThenTheSegmentIsKeptVerbatim(string name)
	{
		Assert.True(SymbolSignature.TryParse(name, out SymbolSignatureQuery query));

		int lastDot = name.LastIndexOf('.');
		Assert.Equal(name[(lastDot + 1)..], query.SimpleName);
		Assert.Equal(-1, query.Arity);
	}

	[Theory]
	[InlineData("Repro.Box<>", 1)]
	[InlineData("Repro.Pair<,>", 2)]
	public void WhenAnUnboundGenericIsWritten_ThenEachTopLevelCommaCountsTowardsTheArity(string name, int expectedArity)
	{
		Assert.True(SymbolSignature.TryParse(name, out SymbolSignatureQuery query));

		Assert.Equal(expectedArity, query.Arity);
	}

	[Fact]
	public void WhenAQualifiedTypeNameOmitsArityAndOneArityExists_ThenItMatchesRelaxed()
	{
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box", Type("Repro.Box`1")));
	}

	[Fact]
	public void WhenANonGenericTypeAndAGenericShareAName_ThenOnlyTheNonGenericMatchesExact()
	{
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, Classify("Repro.Pair", Type("Repro.Pair")));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Pair", Type("Repro.Pair`1")));
	}

	[Fact]
	public void WhenAQueryHasSeveralGenericArities_ThenBothMatchRelaxed()
	{
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Multi", Type("Repro.Multi`1")));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Multi", Type("Repro.Multi`2")));
	}

	[Fact]
	public void WhenAContainingSegmentOmitsArity_ThenTheMemberMatchesRelaxed()
	{
		IMethodSymbol get = Type("Repro.Box`1").GetMembers("Get").OfType<IMethodSymbol>().Single();

		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box.Get", get));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box`1.Get", get));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box.Get()", get));
	}

	[Fact]
	public void WhenAContainingSegmentCarriesAWrittenArity_ThenItConstrains()
	{
		IMethodSymbol get = Type("Repro.Box`1").GetMembers("Get").OfType<IMethodSymbol>().Single();

		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, Classify("Repro.Box<T>.Get", get));
		Assert.Equal(SymbolSignature.SymbolMatchKind.None, Classify("Repro.Pair<A, B>.Get", get));
	}

	[Fact]
	public void WhenTypeParametersAreRenamedOrConstructed_ThenTheArityStillConstrains()
	{
		INamedTypeSymbol box = Type("Repro.Box`1");

		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box<X>", box));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box<int>", box));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box`1", box));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Box<>", box));
		Assert.Equal(SymbolSignature.SymbolMatchKind.None, Classify("Repro.Box`2", box));
		Assert.Equal(SymbolSignature.SymbolMatchKind.None, Classify("Repro.Pair<A, B>", box));
	}

	[Fact]
	public void WhenAnUnqualifiedNameCarriesAnArity_ThenOnlyThatArityMatches()
	{
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, Classify("Pair`1", Type("Repro.Pair`1")));
		Assert.Equal(SymbolSignature.SymbolMatchKind.None, Classify("Pair`1", Type("Repro.Pair")));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, Classify("Pair`0", Type("Repro.Pair")));
	}

	[Fact]
	public void WhenASegmentCountDiffers_ThenNothingMatches()
	{
		// A constructor renders under the type name as a third segment, so a bare type query cannot collide
		// with it — and a type in a deeper namespace is a different segment count too.
		IMethodSymbol constructor = Type("Repro.Box`1").InstanceConstructors.Single(constructor => !constructor.IsImplicitlyDeclared);

		Assert.Equal(SymbolSignature.SymbolMatchKind.None, Classify("Repro.Box", constructor));
	}

	[Fact]
	public void WhenALocalFunctionContainerIsGenericAndWrittenBare_ThenItMatchesRelaxed()
	{
		IMethodSymbol parse = Type("Repro.Outer`1+Inner`1").GetMembers("Parse").OfType<IMethodSymbol>().Single();
		SyntaxTree tree = parse.DeclaringSyntaxReferences[0].SyntaxTree;
		SemanticModel model = Compilation.GetSemanticModel(tree);
		IMethodSymbol tokenize = tree.GetRoot()
			.DescendantNodes()
			.OfType<LocalFunctionStatementSyntax>()
			.Select(node => model.GetDeclaredSymbol(node)!)
			.OfType<IMethodSymbol>()
			.First(candidate => candidate.Name == "Tokenize");

		Assert.Equal(SymbolSignature.SymbolMatchKind.Relaxed, Classify("Repro.Outer.Inner.Parse.Tokenize", tokenize));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, Classify("Repro.Outer<T>.Inner<U>.Parse.Tokenize", tokenize));
	}

	[Fact]
	public void WhenACandidateStringIsSentBack_ThenItMatchesItsOwnSymbolExactly()
	{
		INamedTypeSymbol multi = Type("Repro.Multi`2");

		Assert.True(SymbolSignature.TryParse(SymbolSignature.Of(multi), out SymbolSignatureQuery query));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, SymbolSignature.Classify(multi, query));
	}

	private static SymbolSignature.SymbolMatchKind Classify(string name, ISymbol symbol)
	{
		Assert.True(SymbolSignature.TryParse(name, out SymbolSignatureQuery query));
		return SymbolSignature.Classify(symbol, query);
	}

	private static INamedTypeSymbol Type(string metadataName) =>
		Compilation.GetTypeByMetadataName(metadataName)
		?? throw new InvalidOperationException($"'{metadataName}' is missing from the test compilation.");

	private static readonly CSharpCompilation Compilation = CSharpCompilation.Create(
		"SymbolSignatureGenericTests",
		[CSharpSyntaxTree.ParseText(Source)],
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
}
