using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class FindReferencesTests
{
	[Fact]
	public async Task WhenAReferencedTypeIsRequested_ThenTheOutlineNestsItByFileNamespaceTypeAndMember()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("resolvedSymbol=SimpleLibrary.Greeter", result);
		Assert.DoesNotContain("truncated", result);
		Assert.DoesNotContain("count", result);
		Assert.Contains(result.Split('\n'), line => line == "SimpleLibrary");
		Assert.Contains(result.Split('\n'), line => line == "\tSimpleLibrary");
		Assert.Contains(result.Split('\n'), line => line == "\t\tCaller.cs");
		Assert.Contains("\t\t\t\tclass,Caller\n", result);
		Assert.Contains("\t\t\t\t\tmethod,Run,5:", result);
		Assert.DoesNotContain("\r", result);
	}
	[Fact]
	public async Task WhenTheSymbolIsNotFound_ThenANotFoundHeaderIsReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
	}
	[Fact]
	public async Task WhenTheLoadWaitTimeoutElapses_ThenAnIndexingHeaderIsReturned()
	{
		using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenManyReferencesShareDeclarations_ThenTheyNestUnderTheirContainingTypeAndMember()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.References);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.References, "RefSpace.IThing");

		Assert.Contains("resolvedSymbol=RefSpace.IThing", result);
		Assert.DoesNotContain("count", result);
		Assert.DoesNotContain("truncated", result);
		Assert.DoesNotContain("\r", result);

		Assert.Contains(result.Split('\n'), line => line == "\t\tThings.cs");
		Assert.Contains(result.Split('\n'), line => line == "\t\tOuter.cs");
		Assert.Contains("\t\t\tRefSpace\n", result);

		// A type referenced in its own base list carries a location; Alpha/Beta sit at namespace depth.
		Assert.Contains("\t\t\t\tclass,Alpha,", result);
		Assert.Contains("\t\t\t\tclass,Beta,", result);

		// Outer is referenced nowhere itself, so it is a parent-only line; Nested nests one level deeper.
		Assert.Contains("\t\t\t\tclass,Outer\n", result);
		Assert.Contains("\t\t\t\t\tclass,Nested,", result);

		// Methods nest under their type; a method declaring two locals plus a return type lists three locations.
		Assert.Equal(3, LocationCountUnder(result, "method,AlphaPair"));
		Assert.Equal(3, LocationCountUnder(result, "method,NestedPair"));
	}

	private static int LocationCountUnder(string text, string declaration)
	{
		foreach (string line in text.Split('\n'))
		{
			string trimmed = line.TrimStart('\t');
			if (!trimmed.StartsWith(declaration + ",", StringComparison.Ordinal))
				continue;

			// trimmed is "kind,name,loc|loc|...": kind and name have no commas, so the third field is the list.
			string[] parts = trimmed.Split(',');
			return parts[2].Split('|').Length;
		}

		return -1;
	}
	[Fact]
	public async Task WhenAnOverloadIsTargetedBySignature_ThenOnlyThatOverloadsReferencesAreReturned()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Ledger.Add(int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Ledger.Add(int)", result);
	}
	[Fact]
	public async Task WhenTheNameMatchesSeveralOverloads_ThenTheCandidatesAreAcceptedBackVerbatim()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string ambiguous = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Ledger.Add");
		Assert.Contains("error=Ambiguous", ambiguous);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add(int, int)\n", ambiguous);

		string resolved = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Ledger.Add(int, int)");
		Assert.DoesNotContain("error=", resolved);
	}
	[Fact]
	public async Task WhenCountOnlyIsRequested_ThenCountsAndPerKindLinesAreReturnedWithoutReferences()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string normal = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");
		string counted = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Widget.Compute", countOnly: true);

		// The reference sits inside UseCompute, so the normal body has one method leaf.
		int items = normal.Split('\n').Count(line => line.TrimStart('\t').StartsWith("method,UseCompute", StringComparison.Ordinal));
		Assert.True(items > 0, normal);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Widget.Compute", counted);
		Assert.Contains($"count={items}", counted);
		Assert.Contains($"count.method={items}", counted);
		Assert.DoesNotContain("UseCompute", counted);
		Assert.DoesNotContain("\n\n", counted);
	}

}
