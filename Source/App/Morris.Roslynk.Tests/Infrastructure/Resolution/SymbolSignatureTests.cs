using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

public class SymbolSignatureTests
{
	private const string Source =
		"""
		namespace Sample
		{
			public class Target
			{
				public void M() { }
				public void M(int value) { }
				public void M(string value, int times) { }
				public void M(int? value) { }
				public void M(ref int value) { }
				public void M(string? value, bool flag) { }
				public void M<T>(T value) { }
				public void M(System.Collections.Generic.List<int> values) { }
				public int this[int index] => index;
			}

			public class Colliding
			{
				public void Pick(First.Thing thing) { }
				public void Pick(Second.Thing thing) { }
			}
		}

		namespace Sample.First { public class Thing { } }
		namespace Sample.Second { public class Thing { } }
		""";

	[Fact]
	public void WhenAMethodIsRendered_ThenParameterTypesUseKeywordAliases()
	{
		Assert.Equal("Sample.Target.M(string, int)", Render("M", parameterCount: 2, first: "string"));
		Assert.Equal("Sample.Target.M()", Render("M", parameterCount: 0));
	}

	[Fact]
	public void WhenRenderedFullyQualified_ThenParameterTypesCarryTheirNamespace()
	{
		IMethodSymbol method = Method("M", parameterCount: 1, first: "List<int>");

		Assert.Equal(
			"Sample.Target.M(System.Collections.Generic.List<System.Int32>)",
			SymbolSignature.Of(method, SignatureTier.FullyQualified));
	}

	[Fact]
	public void WhenAnIndexerIsRendered_ThenBracketsAreUsed()
	{
		IPropertySymbol indexer = Type("Target").GetMembers().OfType<IPropertySymbol>().Single(member => member.IsIndexer);

		Assert.Equal("Sample.Target.this[int]", SymbolSignature.Of(indexer));
	}

	[Fact]
	public void WhenAGenericMethodIsRendered_ThenItsTypeParametersArePresent()
	{
		IMethodSymbol method = Type("Target").GetMembers("M").OfType<IMethodSymbol>().Single(candidate => candidate.Arity == 1);

		Assert.Equal("Sample.Target.M<T>(T)", SymbolSignature.Of(method));
	}

	[Fact]
	public void WhenAClassIsRendered_ThenNoParameterListIsAdded()
	{
		Assert.Equal("Sample.Target", SymbolSignature.Of(Type("Target")));
	}

	[Fact]
	public void WhenOverloadsAreDistinguished_ThenEachRendersDifferently()
	{
		IReadOnlyList<string> candidates =
			SymbolSignature.Distinguish(Type("Target").GetMembers("M").OfType<IMethodSymbol>());

		Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
		Assert.Contains("Sample.Target.M(int)", candidates);
		Assert.Contains("Sample.Target.M(int?)", candidates);
	}

	[Fact]
	public void WhenTypesCollideAcrossNamespaces_ThenCandidatesEscalateToFullyQualified()
	{
		IReadOnlyList<string> candidates =
			SymbolSignature.Distinguish(Type("Colliding").GetMembers("Pick").OfType<IMethodSymbol>());

		Assert.Equal(2, candidates.Count);
		Assert.Contains("Sample.Colliding.Pick(Sample.First.Thing)", candidates);
		Assert.Contains("Sample.Colliding.Pick(Sample.Second.Thing)", candidates);
	}

	[Fact]
	public void WhenASignatureIsWrittenWithParameterNamesAndDefaults_ThenTheyAreIgnored()
	{
		IMethodSymbol method = Method("M", parameterCount: 2, first: "string");

		Assert.True(Matches(method, "Sample.Target.M(string value, int times = 3)"));
		Assert.True(Matches(method, "Sample.Target.M( string , int )"));
	}

	[Fact]
	public void WhenASignatureUsesFullyQualifiedParameterTypes_ThenItStillMatches()
	{
		IMethodSymbol method = Method("M", parameterCount: 1, first: "int");

		Assert.True(Matches(method, "Sample.Target.M(System.Int32)"));
		Assert.True(Matches(method, "Sample.Target.M(global::System.Int32)"));
	}

	[Fact]
	public void WhenANullableReferenceAnnotationIsWritten_ThenMatchingIsUnaffected()
	{
		IMethodSymbol method = Method("M", parameterCount: 2, first: "string?");

		Assert.True(Matches(method, "Sample.Target.M(string, bool)"));
		Assert.True(Matches(method, "Sample.Target.M(string?, bool)"));
	}

	[Fact]
	public void WhenANullableValueTypeIsWritten_ThenItDoesNotMatchTheNonNullableOverload()
	{
		IMethodSymbol nullable = Method("M", parameterCount: 1, first: "int?");
		IMethodSymbol plain = Method("M", parameterCount: 1, first: "int");

		Assert.True(Matches(nullable, "Sample.Target.M(int?)"));
		Assert.True(Matches(nullable, "Sample.Target.M(Nullable<int>)"));
		Assert.False(Matches(nullable, "Sample.Target.M(int)"));
		Assert.False(Matches(plain, "Sample.Target.M(int?)"));
	}

	[Fact]
	public void WhenARefModifierIsWritten_ThenOnlyTheMatchingOverloadMatches()
	{
		IMethodSymbol byRef = Type("Target").GetMembers("M").OfType<IMethodSymbol>()
			.Single(candidate => candidate.Parameters.Length == 1 && candidate.Parameters[0].RefKind == RefKind.Ref);
		IMethodSymbol byValue = Method("M", parameterCount: 1, first: "int");

		Assert.True(Matches(byRef, "Sample.Target.M(ref int)"));
		Assert.False(Matches(byValue, "Sample.Target.M(ref int)"));

		// Written without a modifier, the name is modifier-agnostic and both overloads match.
		Assert.True(Matches(byRef, "Sample.Target.M(int)"));
	}

	[Fact]
	public void WhenNoParameterListIsWritten_ThenEveryOverloadMatches()
	{
		Assert.True(SymbolSignature.TryParse("Sample.Target.M", out SymbolSignatureQuery query));
		Assert.Equal(ParameterListKind.None, query.ListKind);
		Assert.All(
			Type("Target").GetMembers("M").OfType<IMethodSymbol>(),
			method => Assert.True(SymbolSignature.Matches(method, query)));
	}

	[Fact]
	public void WhenEmptyParenthesesAreWritten_ThenOnlyTheZeroArgOverloadMatches()
	{
		Assert.True(Matches(Method("M", parameterCount: 0), "Sample.Target.M()"));
		Assert.False(Matches(Method("M", parameterCount: 1, first: "int"), "Sample.Target.M()"));
	}

	[Fact]
	public void WhenTheNameContainsDotsAndCommasInsideBrackets_ThenTheHeadSplitsAtTheOuterDot()
	{
		Assert.True(SymbolSignature.TryParse("N.Repo.Get(System.Collections.Generic.Dictionary<string, int>)", out SymbolSignatureQuery query));

		Assert.Equal("N.Repo.Get", query.QualifiedName);
		Assert.Equal("Get", query.SimpleName);
		Assert.Single(query.Parameters);
		Assert.Equal("System.Collections.Generic.Dictionary<string, int>", query.Parameters[0].TypeText);
	}

	[Fact]
	public void WhenAGenericArityIsWritten_ThenItIsParsedOffTheSimpleName()
	{
		Assert.True(SymbolSignature.TryParse("N.Repo.Get<T>(T)", out SymbolSignatureQuery query));

		Assert.Equal("Get", query.SimpleName);
		Assert.Equal(1, query.Arity);
	}

	[Fact]
	public void WhenAGenericArityIsWritten_ThenOnlyAMethodOfThatArityMatches()
	{
		IMethodSymbol generic = Type("Target").GetMembers("M").OfType<IMethodSymbol>().Single(candidate => candidate.Arity == 1);

		Assert.True(Matches(generic, "Sample.Target.M<T>(T)"));
		Assert.False(Matches(Method("M", parameterCount: 1, first: "int"), "Sample.Target.M<T>(T)"));
	}

	[Fact]
	public void WhenAnIndexerSignatureIsWritten_ThenParenthesesDoNotMatchIt()
	{
		IPropertySymbol indexer = Type("Target").GetMembers().OfType<IPropertySymbol>().Single(member => member.IsIndexer);

		Assert.True(Matches(indexer, "Sample.Target.this[int]"));
		Assert.False(Matches(indexer, "Sample.Target.this(int)"));
	}

	[Fact]
	public void WhenTheNameIsBlankOrTheBracketsAreUnbalanced_ThenItDoesNotParse()
	{
		Assert.False(SymbolSignature.TryParse(null, out _));
		Assert.False(SymbolSignature.TryParse("   ", out _));
		Assert.False(SymbolSignature.TryParse("N.T.M int)", out _));
	}

	private static bool Matches(ISymbol symbol, string name)
	{
		Assert.True(SymbolSignature.TryParse(name, out SymbolSignatureQuery query));
		return SymbolSignature.Matches(symbol, query);
	}

	private static string Render(string methodName, int parameterCount, string? first = null) =>
		SymbolSignature.Of(Method(methodName, parameterCount, first));

	/// <summary>
	/// The overload of <paramref name="name"/> with the given parameter count, narrowed by its first
	/// parameter's minimally-qualified type when several overloads share that count.
	/// </summary>
	private static IMethodSymbol Method(string name, int parameterCount, string? first = null) =>
		Type("Target").GetMembers(name)
			.OfType<IMethodSymbol>()
			.Where(candidate => candidate.Arity == 0)
			.Where(candidate => candidate.Parameters.Length == parameterCount)
			.Single(candidate => first is null
				|| (candidate.Parameters[0].RefKind == RefKind.None
					&& candidate.Parameters[0].Type.ToDisplayString(FirstParameterFormat) == first));

	private static readonly SymbolDisplayFormat FirstParameterFormat = new(
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
			| SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	private static INamedTypeSymbol Type(string name) =>
		Compilation.GetTypeByMetadataName($"Sample.{name}")
		?? throw new InvalidOperationException($"'{name}' is missing from the test compilation.");

	private static readonly CSharpCompilation Compilation = CSharpCompilation.Create(
		"SymbolSignatureTests",
		[CSharpSyntaxTree.ParseText(Source)],
		[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
}
