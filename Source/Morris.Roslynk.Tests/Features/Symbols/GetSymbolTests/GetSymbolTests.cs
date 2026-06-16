using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolTests;

public class GetSymbolTests
{
	[Fact]
	public async Task WhenATypeIsRequested_ThenLeanReturnsPathLocAndTheDeclaratorThroughItsBaseList()
	{
		string result = await RunAsync("SimpleLibrary.Greeter");

		Assert.Contains("#path=SimpleLibrary/Greeter.cs", result);
		Assert.Contains("#loc=", result);
		Assert.Contains("public class Greeter : IGreeter", result);
		Assert.DoesNotContain("#fullName=", result);
		Assert.DoesNotContain("#status=", result);
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
	public async Task WhenAMetadataSymbolIsRequested_ThenKindSignatureAndAssemblyAreReturnedWithoutAPath()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("#kind=class", result);
		Assert.Contains("#signature=", result);
		Assert.Contains("#assembly=", result);
		Assert.DoesNotContain("#path=", result);
	}

	[Fact]
	public async Task WhenFormatIsFull_ThenTheFullyQualifiedFieldsAreReturned()
	{
		string result = await RunAsync("SimpleLibrary.Greeter", format: "full");

		Assert.Contains("#fullName=SimpleLibrary.Greeter", result);
		Assert.Contains("#accessibility=public", result);
		Assert.Contains("#source=source", result);
		Assert.Contains("#location=", result);
	}

	[Fact]
	public async Task WhenFormatIsFullAndTheSymbolIsDocumented_ThenTheSummaryIsIncluded()
	{
		string result = await RunAsync("SimpleLibrary.Widget.Compute", format: "full");

		Assert.Contains("summary: ", result);
	}

	[Fact]
	public async Task WhenComparingLeanToFull_ThenLeanIsShorter()
	{
		string lean = await RunAsync("SimpleLibrary.Greeter.Greet");
		string full = await RunAsync("SimpleLibrary.Greeter.Greet", format: "full");

		// Lean drops the 8 headline fields (and doc) for path + loc + the declarator: ~75% fewer chars here.
		Assert.True(lean.Length < full.Length, $"lean ({lean.Length}) should be shorter than full ({full.Length})");
	}

	[Fact]
	public async Task WhenTheSymbolDoesNotExist_ThenNotFoundIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.DoesNotExist");

		Assert.Contains("#error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheNameNearlyMatches_ThenRankedCandidatesAreSuggested()
	{
		string result = await RunAsync("SimpleLibrary.Greet");

		Assert.Contains("#error=NotFound", result);
		Assert.Contains("#candidate=SimpleLibrary.Greeter", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		string result = await subject.GetSymbol(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static async Task<string> RunAsync(string symbolName, string format = "lean")
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolTool(registry, new SymbolResolver());

		return await subject.GetSymbol(TestSolutions.Simple, symbolName, format);
	}
}
