using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

public class ErrorTypeCountTests
{
	[Fact]
	public void WhenEveryTypeInTheSignatureBinds_ThenTheCountIsZero()
	{
		IMethodSymbol method = Method(
			"""
			namespace Sample
			{
				public class Target
				{
					public void M(int value, string text) { }
				}
			}
			""",
			"M");

		Assert.Equal(0, ErrorTypeCount.Of(method));
	}

	[Fact]
	public void WhenAParameterTypeDoesNotBind_ThenItCountsOnce()
	{
		IMethodSymbol method = Method(
			"""
			namespace Sample
			{
				public class Target
				{
					public void M(Missing value) { }
				}
			}
			""",
			"M");

		Assert.Equal(1, ErrorTypeCount.Of(method));
	}

	[Fact]
	public void WhenOnlyAGenericArgumentDoesNotBind_ThenItIsCounted()
	{
		IMethodSymbol method = Method(
			"""
			namespace Sample
			{
				public class Wrapper<T> { }

				public class Target
				{
					public void M(Wrapper<Missing> value) { }
				}
			}
			""",
			"M");

		Assert.Equal(1, ErrorTypeCount.Of(method));
	}

	[Fact]
	public void WhenTheReturnTypeDoesNotBind_ThenItIsCounted()
	{
		IMethodSymbol method = Method(
			"""
			namespace Sample
			{
				public class Target
				{
					public Missing M() => throw null;
				}
			}
			""",
			"M");

		Assert.Equal(1, ErrorTypeCount.Of(method));
	}

	[Fact]
	public void WhenALocalFunctionsContainerParameterDoesNotBind_ThenItIsCounted()
	{
		CSharpCompilation compilation = Compile(
			"""
			namespace Sample
			{
				public class Target
				{
					public void Outer(Missing value)
					{
						void Local() { }
					}
				}
			}
			""");
		SyntaxTree tree = compilation.SyntaxTrees[0];
		LocalFunctionStatementSyntax declaration = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
		IMethodSymbol local = compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration)!;

		// The local's own signature is clean; the count comes from its container's parameter.
		Assert.Equal(1, ErrorTypeCount.Of(local));
	}

	[Fact]
	public void WhenABaseTypeDoesNotBind_ThenItIsCounted()
	{
		INamedTypeSymbol type = Type(
			"""
			namespace Sample
			{
				public class Derived : Missing { }
			}
			""",
			"Sample.Derived");

		Assert.Equal(1, ErrorTypeCount.Of(type));
	}

	[Fact]
	public void WhenATypeIsItsOwnInterfaceArgument_ThenCountingTerminates()
	{
		INamedTypeSymbol type = Type(
			"""
			using System;

			namespace Sample
			{
				public class SelfComparable : IComparable<SelfComparable>
				{
					public int CompareTo(SelfComparable? other) => 0;
				}
			}
			""",
			"Sample.SelfComparable");

		Assert.Equal(0, ErrorTypeCount.Of(type));
	}

	[Fact]
	public void WhenTheSymbolItselfIsUnresolved_ThenItIsCounted()
	{
		// A parameter whose type did not bind is itself an error type (this is what a position in a target
		// framework where the identifier does not bind resolves to); it renders as written, exactly like one
		// of its arguments would.
		IMethodSymbol method = Method(
			"""
			namespace Sample
			{
				public class Target
				{
					public void M(Missing value) { }
				}
			}
			""",
			"M");
		ITypeSymbol parameterType = method.Parameters[0].Type;

		Assert.True(parameterType is IErrorTypeSymbol, "The parameter must be an error type, or this test proves nothing.");
		Assert.Equal(1, ErrorTypeCount.Of(parameterType));
	}

	private static IMethodSymbol Method(string source, string methodName) =>
		Type(source).GetMembers(methodName).OfType<IMethodSymbol>().Single();

	private static INamedTypeSymbol Type(string source, string metadataName = "Sample.Target") =>
		Compile(source).GetTypeByMetadataName(metadataName)
			?? throw new InvalidOperationException($"'{metadataName}' is missing from the test compilation.");

	private static CSharpCompilation Compile(string source) =>
		CSharpCompilation.Create(
			nameof(ErrorTypeCountTests),
			[CSharpSyntaxTree.ParseText(source)],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
