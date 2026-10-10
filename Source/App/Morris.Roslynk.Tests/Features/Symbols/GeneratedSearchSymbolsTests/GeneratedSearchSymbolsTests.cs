using System.Text.RegularExpressions;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Features.Symbols.GeneratedSearchSymbolsTests;

public class GeneratedSearchSymbolsTests
{
	[Theory]
	[InlineData("GreetingCsv", "class,GreetingCsv,1:52", "Greeting.csv.g.cs,generated=Y")]
	[InlineData("Hello", "class,Hello,1:52", "Hello.g.cs,generated=Y")]
	[InlineData("OnlyGenerated", "class,OnlyGenerated,23:22", "Shape.g.cs,generated=Y")]
	[InlineData("GeneratedOnly", "namespace,GeneratedOnly,21:11", "Shape.g.cs,generated=Y")]
	[InlineData("ReadArea", "method,ReadArea,5:14", "Shape.g.cs,generated=Y")]
	[InlineData("TryResolve", "method,TryResolve,16:16", "Shape.g.cs,generated=Y")]
	public async Task WhenOnlyAGeneratedDeclarationMatchesTheQuery_ThenSearchSymbolsReturnsItMarkedGenerated(string query, string leaf, string file)
	{
		string result = await SearchAsync(query);

		Assert.DoesNotContain("error=", result);
		Assert.Contains(leaf, result);
		Assert.Contains(file, result);
	}

	[Fact]
	public async Task WhenAGeneratedNamespaceMatchesTheQuery_ThenItIsListedLikeAHandWrittenNamespace()
	{
		// GeneratedNamespace is declared by every generated document; which one holds the symbol's first
		// location follows the generators' run order, so only the namespace leaf and its marking are pinned.
		string result = await SearchAsync("GeneratedNamespace");

		Assert.Contains("namespace,GeneratedNamespace,", result);
		Assert.Contains("generated=Y", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberOfANestedTypeMatches_ThenItNestsUnderItsGeneratedContainers()
	{
		string result = await SearchAsync("TryResolve");

		Assert.Contains("class,Shape\n", result);
		Assert.Contains("class,Cache\n", result);
		Assert.Contains("method,TryResolve,16:16", result);
	}

	[Fact]
	public async Task WhenALocalFunctionInAGeneratedMemberMatches_ThenItNestsUnderThatMember()
	{
		string result = await SearchAsync("Format");

		Assert.Contains("Shape.g.cs,generated=Y", result);
		Assert.Contains("method,Describe\n", result);
		Assert.Contains("localfunction,Format,11:18", result);
	}

	[Fact]
	public async Task WhenAPartialTypeHasAGeneratedPart_ThenItIsListedOnceAtItsFirstLocation()
	{
		// The declaration search and the generated pass return the same merged symbol, so the identity index
		// keeps a single leaf: like any hand-written partial, it renders at the symbol's first location (the
		// hand-written part), and the generated part shows through its own members (ReadArea, Describe, ...).
		string result = await SearchAsync("Shape");

		Assert.Single(Regex.Matches(result, "class,Shape,"));
		Assert.Contains("Shape.cs\n", result);
		Assert.DoesNotContain("Shape.g.cs", result);
		Assert.DoesNotContain("count=", result);
	}

	[Fact]
	public async Task WhenAQueryMatchesHandWrittenAndGeneratedDeclarations_ThenEachIsListedExactlyOnce()
	{
		string result = await SearchAsync("Greeting");

		Assert.Single(Regex.Matches(result, "field,Greeting,"));
		Assert.Single(Regex.Matches(result, "method,Greeting,"));
		Assert.Single(Regex.Matches(result, "method,CsvGreeting,"));
		Assert.Single(Regex.Matches(result, "class,GreetingCsv,"));
		Assert.DoesNotContain("count=", result);
	}

	[Fact]
	public async Task WhenAnAccessorStyleQueryIsUsed_ThenNothingLeaksFromTheGeneratedCompilation()
	{
		// The generated pass applies the same post-filter Roslyn applies to its own results: no accessors.
		string result = await SearchAsync("get_");

		Assert.Equal("", result);
	}

	[Fact]
	public async Task WhenMaxResultsCapsTheMatches_ThenCountAndTruncatedAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// Shape matches the partial type and the ShapeGenerator class, so the cap bites.
		string result = await subject.SearchSymbols(TestSolutions.Generator, "Shape", maxResults: 1);

		Assert.StartsWith("count=", result);
		Assert.Contains("truncated=Y", result);
	}

	private static async Task<string> SearchAsync(string query)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		return await new SearchSymbolsTool(registry, new ProjectionService()).SearchSymbols(TestSolutions.Generator, query);
	}
}
