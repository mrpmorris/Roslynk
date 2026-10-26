using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Features.Symbols.SearchSymbolsTests;

public class SearchSymbolsTests
{
	[Fact]
	public async Task WhenSearchingByNameSubstring_ThenMatchingSymbolsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("class,Greeter", result);
	}
	[Fact]
	public async Task WhenTheFilterIsSuppliedByItsPublishedName_ThenTheFilterApplies()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// Pins the public parameter name (issue #152): 'query' read as free-text search, so a caller
		// sent {query: "objVM", kinds: ["Method"]} expecting objVM's methods and got nothing.
		string result = await subject.SearchSymbols(TestSolutions.Simple, symbolNameFilter: "Greet");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Greeter", result);
	}
	[Fact]
	public async Task WhenNothingMatchesTheQuery_ThenNoResultsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "NoSuchSymbolNameHere");

		OutlineAssert.HasNoSymbols(result);
	}
	[Fact]
	public async Task WhenTheLoadWaitTimeoutElapses_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
	[Fact]
	public async Task WhenAQueryMatchesSeveralOverloads_ThenEveryOverloadIsListed()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Add");

		Assert.DoesNotContain("error=", result);
		// Both Ledger.Add overloads are listed; before the dedupe key carried the signature, one was dropped.
		Assert.Equal(2, result.Split('\n').Count(line => line.TrimStart('	').StartsWith("method,Add,", StringComparison.Ordinal)));
	}
	[Fact]
	public async Task WhenAQueryNamesAMemberUnderItsType_ThenOnlyThatMemberIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Widget.Compute");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Widget", result);
		Assert.Contains("method,Compute,8:13", result);
		// The dotted query names the member itself, so the substring-matching sibling is not listed.
		Assert.DoesNotContain("UseCompute", result);
	}
	[Fact]
	public async Task WhenAQueryNamesAMemberByItsNamespaceAndType_ThenThatMemberIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "SimpleLibrary.Ledger.Total");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Ledger", result);
		Assert.Contains("property,Total,5:13", result);
	}
	[Fact]
	public async Task WhenAQueryNamesAMemberByANestedTypeChain_ThenThatMemberIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.LocalFunctions);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.LocalFunctions, "ParentClass.Widget.Caller");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,ParentClass", result);
		Assert.Contains("class,Widget", result);
		Assert.Contains("method,Caller", result);
	}
	[Fact]
	public async Task WhenADottedQueryUsesDifferentCasing_ThenTheMemberIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "widget.COMPUTE");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Compute,8:13", result);
	}
	[Fact]
	public async Task WhenADottedQueryNamesAMemberOfAnotherType_ThenNoResultsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// Add exists, but on Calculator and Ledger, not Widget.
		string result = await subject.SearchSymbols(TestSolutions.Simple, "Widget.Add");

		OutlineAssert.HasNoSymbols(result);
	}
	[Fact]
	public async Task WhenADottedQueryNamesAMissingMember_ThenNoResultsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Widget.NoSuchMember");

		OutlineAssert.HasNoSymbols(result);
	}
	[Fact]
	public async Task WhenADottedQueryWritesOnlyPartOfAContainerSegment_ThenNoResultsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// 'Library' is not a whole segment of 'SimpleLibrary', so the suffix comparison must not cross it.
		string result = await subject.SearchSymbols(TestSolutions.Simple, "Library.Widget.Compute");

		OutlineAssert.HasNoSymbols(result);
	}
	[Fact]
	public async Task WhenABareQueryMatchesMembersOfSeveralTypes_ThenEveryContainerIsListed()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Add");

		Assert.Contains("class,Calculator", result);
		Assert.Contains("class,Ledger", result);
	}
	[Fact]
	public async Task WhenADottedQuerySelectsOneOfSeveralContainers_ThenOnlyThatContainerIsListed()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Ledger.Add");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Ledger", result);
		Assert.DoesNotContain("Calculator", result);
		Assert.Equal(1, result.Split('\n').Count(line => line.TrimStart('	').StartsWith("method,Add,", StringComparison.Ordinal)));
	}
	[Fact]
	public async Task WhenKindsFiltersToMethods_ThenOnlyMethodsAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// "Greet" matches both the Greeter class and its Greet method; only the method was requested.
		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet", kinds: ["Method"]);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Greet,", result);
		Assert.DoesNotContain("class,Greeter,", result);
	}
	[Fact]
	public async Task WhenKindsFiltersToClasses_ThenMembersAreNotReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet", kinds: ["Class"]);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Greeter,", result);
		Assert.DoesNotContain("method,Greet,", result);
	}
	[Fact]
	public async Task WhenKindsUsesPascalCase_ThenTheFilterStillApplies()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet", kinds: ["METHOD"]);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Greet,", result);
		Assert.DoesNotContain("class,Greeter,", result);
	}
	[Fact]
	public async Task WhenKindsIsEmpty_ThenEveryKindIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet", kinds: []);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Greeter,", result);
		Assert.Contains("method,Greet,", result);
	}
	[Fact]
	public async Task WhenAKindNameIsUnknown_ThenTheCallIsInvalidAndNamesTheAcceptedKinds()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// A silently unmatched name would look like "no such symbol"; the caller's typo must be named.
		FormatException exception = await Assert.ThrowsAsync<FormatException>(
			() => subject.SearchSymbols(TestSolutions.Simple, "Greet", kinds: ["Methods"]));

		Assert.Contains("'kinds'", exception.Message, StringComparison.Ordinal);
		Assert.Contains("'Methods'", exception.Message, StringComparison.Ordinal);
		Assert.Contains("method", exception.Message, StringComparison.Ordinal);
	}
	[Fact]
	public async Task WhenCountOnlyIsRequested_ThenCountEqualsTheNormalSearchesItemCount()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string normal = await subject.SearchSymbols(TestSolutions.Simple, "Add");
		string counted = await subject.SearchSymbols(TestSolutions.Simple, "Add", countOnly: true);

		// Every match here is a leaf under a container that carries no location of its own, so the unpaged
		// item count is the number of leaf lines.
		int items = normal.Split('\n').Count(line => line.TrimStart('\t').StartsWith("method,Add,", StringComparison.Ordinal));
		Assert.True(items > 0, normal);
		Assert.Equal($"count={items}\ncount.method={items}\n", counted);
	}
}
