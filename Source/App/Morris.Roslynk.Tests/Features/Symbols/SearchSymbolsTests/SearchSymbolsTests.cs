using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Features.Symbols.SearchSymbolsTests;

public class SearchSymbolsTests
{
	[Fact]
	public async Task WhenSearchingByNameSubstring_ThenMatchingSymbolsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Greet");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("class,Greeter", result);
	}

	[Fact]
	public async Task WhenNothingMatchesTheQuery_ThenNoResultsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "NoSuchSymbolNameHere");

		Assert.Equal("", result);
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
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Add");

		Assert.DoesNotContain("error=", result);
		// Both Ledger.Add overloads are listed; before the dedupe key carried the signature, one was dropped.
		Assert.Equal(2, result.Split('\n').Count(line => line.TrimStart('	').StartsWith("method,Add,", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task WhenAQueryNamesAMemberUnderItsType_ThenOnlyThatMemberIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
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
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "SimpleLibrary.Ledger.Total");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Ledger", result);
		Assert.Contains("property,Total,5:13", result);
	}

	[Fact]
	public async Task WhenAQueryNamesAMemberByANestedTypeChain_ThenThatMemberIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
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
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "widget.COMPUTE");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Compute,8:13", result);
	}

	[Fact]
	public async Task WhenADottedQueryNamesAMemberOfAnotherType_ThenNoResultsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// Add exists, but on Calculator and Ledger, not Widget.
		string result = await subject.SearchSymbols(TestSolutions.Simple, "Widget.Add");

		Assert.Equal("", result);
	}

	[Fact]
	public async Task WhenADottedQueryNamesAMissingMember_ThenNoResultsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Widget.NoSuchMember");

		Assert.Equal("", result);
	}

	[Fact]
	public async Task WhenADottedQueryWritesOnlyPartOfAContainerSegment_ThenNoResultsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		// 'Library' is not a whole segment of 'SimpleLibrary', so the suffix comparison must not cross it.
		string result = await subject.SearchSymbols(TestSolutions.Simple, "Library.Widget.Compute");

		Assert.Equal("", result);
	}

	[Fact]
	public async Task WhenABareQueryMatchesMembersOfSeveralTypes_ThenEveryContainerIsListed()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Add");

		Assert.Contains("class,Calculator", result);
		Assert.Contains("class,Ledger", result);
	}

	[Fact]
	public async Task WhenADottedQuerySelectsOneOfSeveralContainers_ThenOnlyThatContainerIsListed()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Simple, "Ledger.Add");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Ledger", result);
		Assert.DoesNotContain("Calculator", result);
		Assert.Equal(1, result.Split('\n').Count(line => line.TrimStart('	').StartsWith("method,Add,", StringComparison.Ordinal)));
	}
}
