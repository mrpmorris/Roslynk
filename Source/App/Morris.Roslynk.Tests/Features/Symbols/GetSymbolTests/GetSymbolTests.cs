using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolTests;

public class GetSymbolTests
{
	[Fact]
	public async Task WhenATypeIsRequested_ThenLeanReturnsPathLocAndTheDeclaratorThroughItsBaseList()
	{
		string result = await RunAsync("SimpleLibrary.Greeter");

		Assert.Contains("project=SimpleLibrary\n", result);
		Assert.Contains("path=SimpleLibrary/Greeter.cs", result);
		Assert.Contains("loc=", result);
		Assert.Contains("public class Greeter : IGreeter", result);
		Assert.DoesNotContain("status=", result);
		// A source symbol omits #source (it is the implied common case).
		Assert.DoesNotContain("source=", result);
	}

	[Fact]
	public async Task WhenAnExpressionBodiedMethodIsRequested_ThenTheBodyIsCutAtTheArrow()
	{
		string result = await RunAsync("SimpleLibrary.Widget.Compute");

		Assert.Contains("public int Compute(int value)", result);
		Assert.DoesNotContain("value * 2", result);
	}

	[Fact]
	public async Task WhenAMultiLineSignatureMethodIsRequested_ThenItIsKeptThroughTheClosingParenButNotTheBody()
	{
		string result = await RunAsync("SimpleLibrary.Holder.Combine");

		Assert.Contains("public string Combine(", result);
		Assert.Contains("string second)", result);
		Assert.DoesNotContain("_ready", result);
	}

	[Fact]
	public async Task WhenAnAutoPropertyIsRequested_ThenTheDeclaratorKeepsItsAccessorList()
	{
		string result = await RunAsync("SimpleLibrary.Holder.Count");

		Assert.Contains("public int Count { get; set; }", result);
	}

	[Fact]
	public async Task WhenAFieldIsRequested_ThenTheDeclaratorShowsModifiersTypeAndName()
	{
		string result = await RunAsync("SimpleLibrary.Holder._ready");

		Assert.Contains("private bool _ready", result);
	}

	[Fact]
	public async Task WhenAMetadataSymbolIsRequested_ThenSourceKindSignatureAndAssemblyAreReturnedWithoutAPath()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("source=metadata", result);
		Assert.Contains("kind=class", result);
		Assert.Contains("signature=", result);
		Assert.Contains("assembly=", result);
		Assert.DoesNotContain("path=", result);
		Assert.DoesNotContain("project=", result);
	}

	[Fact]
	public async Task WhenTheSymbolDoesNotExist_ThenNotFoundIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheNameNearlyMatches_ThenRankedCandidatesAreSuggested()
	{
		string result = await RunAsync("SimpleLibrary.Greet");

		Assert.Contains("error=NotFound", result);
		Assert.Contains("candidate=SimpleLibrary.Greeter", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenTheNameMatchesSeveralOverloads_ThenAmbiguousIsReturnedWithDistinguishableCandidates()
	{
		string result = await RunAsync("SimpleLibrary.Ledger.Add");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add(int)\n", result);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add(int, int)\n", result);
	}

	[Fact]
	public async Task WhenAnOverloadIsTargetedBySignature_ThenItsDeclarationIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Ledger.Add(int, int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("public int Add(int amount, int times)", result);
	}

	[Theory]
	[InlineData("Repro.Box", "public class Box<T>")]
	[InlineData("Repro.Box`1", "public class Box<T>")]
	[InlineData("Repro.Pair", "public class Pair")]
	public async Task WhenATypeNameOmitsOrUsesMetadataArity_ThenGetSymbolResolvesIt(string name, string expected)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbol(TestSolutions.MultiTarget, name);

		Assert.DoesNotContain("error=", result);
		Assert.Contains(expected, result);
	}

	[Fact]
	public async Task WhenAContainingTypeOmitsItsArity_ThenTheMemberResolves()
	{
		string result = await RunOnMultiTargetAsync("Repro.Box.Get");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("public T? Get()", result);
	}

	[Fact]
	public async Task WhenSeveralGenericAritiesShareAName_ThenTheBareNameIsAmbiguous()
	{
		string result = await RunOnMultiTargetAsync("Repro.Multi");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=Repro.Multi<T>", result);
		Assert.Contains("candidate=Repro.Multi<T1, T2>", result);
	}

	[Fact]
	public async Task WhenAMemberIsMissing_ThenTheFirstCandidateIsFromTheWrittenContainer()
	{
		string result = await RunOnMultiTargetAsync("Repro.Scanner.read");

		Assert.Contains("error=NotFound", result);
		Assert.True(
			result.IndexOf("candidate=Repro.Scanner.Read", StringComparison.Ordinal)
			< result.IndexOf("candidate=Repro.Converter<T>.Read", StringComparison.Ordinal),
			$"The container's own member should be suggested first: {result}");
	}

	private static async Task<string> RunOnMultiTargetAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetSymbol(TestSolutions.MultiTarget, symbolName);
	}

	private static async Task<string> RunAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetSymbol(TestSolutions.Simple, symbolName);
	}
}
