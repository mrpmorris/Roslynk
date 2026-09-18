using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolBodyTests;

public class GetSymbolBodyTests
{
	[Fact]
	public async Task WhenAMethodIsRequested_ThenTheWholeDeclarationIncludingItsBodyIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Calculator.Add");

		Assert.Contains("project=SimpleLibrary\n", result);
		Assert.Contains("path=SimpleLibrary/Calculator.cs\n", result);
		Assert.Contains("loc=", result);
		Assert.Contains("public int Add(int a, int b)", result);
		Assert.Contains("return a + b;", result);
		Assert.DoesNotContain("error=", result);
	}

	[Fact]
	public async Task WhenTheDeclarationIsIndented_ThenTheSourceTextIsPreservedExactly()
	{
		string result = await RunAsync("SimpleLibrary.Calculator.Add");

		// Original tabs and line breaks survive: the body is the file's text, not a reformatted rendering.
		Assert.Contains("\t{\n\t\treturn a + b;\n\t}", Normalize(result));
	}

	[Fact]
	public async Task WhenLeadingTriviaIsNotRequested_ThenTheTextStartsAtTheDeclaration()
	{
		string result = await RunAsync("SimpleLibrary.Widget.Compute");

		Assert.StartsWith("public int Compute(int value)", Body(result));
		Assert.DoesNotContain("<summary>", result);
	}

	[Fact]
	public async Task WhenLeadingTriviaIsRequested_ThenTheDocumentationCommentIsIncluded()
	{
		string result = await RunAsync("SimpleLibrary.Widget.Compute", includeLeadingTrivia: true);

		Assert.StartsWith("/// <summary>Doubles", Body(result));
		Assert.Contains("public int Compute(int value) => value * 2;", result);
	}

	[Fact]
	public async Task WhenLeadingTriviaIsRequested_ThenTheLocStartsAtTheTriviaNotTheDeclaration()
	{
		string withoutTrivia = await RunAsync("SimpleLibrary.Widget.Compute");
		string withTrivia = await RunAsync("SimpleLibrary.Widget.Compute", includeLeadingTrivia: true);

		Assert.NotEqual(Header(withoutTrivia, "loc"), Header(withTrivia, "loc"));
	}

	[Fact]
	public async Task WhenATypeIsRequested_ThenTheWholeTypeDeclarationIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Greeter");

		Assert.Contains("public class Greeter : IGreeter", result);
		Assert.Contains("public string Greet(string name) => $\"Hello, {name}!\";", result);
	}

	[Fact]
	public async Task WhenAFieldIsRequested_ThenTheWholeFieldDeclarationIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Holder._ready");

		Assert.Contains("private bool _ready;", result);
	}

	[Fact]
	public async Task WhenAPropertyIsRequested_ThenItsAccessorListIsReturnedVerbatim()
	{
		string result = await RunAsync("SimpleLibrary.Ledger.Total");

		Assert.Contains("public int Total { get; private set; }", result);
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredInSeveralParts_ThenEveryPartIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Ledger");

		Assert.Contains("parts=2\n", result);
		Assert.Contains("part=1,project=SimpleLibrary,path=SimpleLibrary/Ledger.cs,loc=", result);
		Assert.Contains("part=2,project=SimpleLibrary,path=SimpleLibrary/Ledger.Totals.cs,loc=", result);
		Assert.Contains("Total += amount;", result);
		Assert.Contains("public int Total { get; private set; }", result);
	}

	[Fact]
	public async Task WhenTheNameMatchesSeveralOverloads_ThenAmbiguousIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Ledger.Add");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add", result);
	}

	[Fact]
	public async Task WhenTheSymbolIsMetadataOnly_ThenNotSupportedIsReturned()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("error=NotSupported", result);
		Assert.DoesNotContain("path=", result);
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
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbolBody(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static string Normalize(string result) => result.Replace("\r\n", "\n");

	private static string Body(string result)
	{
		string normalized = Normalize(result);
		int separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : normalized[(separator + 2)..];
	}

	private static string Header(string result, string key)
	{
		foreach (string line in Normalize(result).Split('\n'))
		{
			if (line.StartsWith(key + "=", StringComparison.Ordinal))
				return line;
		}

		return "";
	}

	[Fact]
	public async Task WhenAnOverloadIsTargetedBySignature_ThenOnlyThatOverloadIsReturned()
	{
		string result = await RunAsync("SimpleLibrary.Ledger.Add(int, int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("path=SimpleLibrary/Ledger.cs\n", result);
		Assert.Contains("public int Add(int amount, int times)", result);
		Assert.DoesNotContain("Adds <paramref", result);
	}

	private static async Task<string> RunAsync(string symbolName, bool includeLeadingTrivia = false)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetSymbolBody(TestSolutions.Simple, symbolName, includeLeadingTrivia);
	}
}
