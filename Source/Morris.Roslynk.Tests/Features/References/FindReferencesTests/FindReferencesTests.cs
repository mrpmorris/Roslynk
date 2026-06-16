using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class FindReferencesTests
{
	[Fact]
	public async Task WhenAReferencedTypeIsRequested_ThenTheOutlineNestsItByFileNamespaceTypeAndMember()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("#resolvedSymbol=SimpleLibrary.Greeter", result);
		Assert.Contains("#truncated=false", result);
		Assert.Contains("SimpleLibrary/Caller.cs", result);
		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("\t\tclass,Caller\n", result);
		Assert.Contains("\t\t\tmethod,Run,5:", result);
		Assert.DoesNotContain("\r", result);
	}

	[Fact]
	public async Task WhenTheSymbolIsNotFound_ThenANotFoundHeaderIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("#error=NotFound", result);
	}

	[Fact]
	public async Task WhenMoreReferencesMatchThanMaxResults_ThenTheHeaderReportsTruncated()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter", maxResults: 0);

		Assert.Contains("#count=0", result);
		Assert.Contains("#truncated=true", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenAnIndexingHeaderIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		string result = await subject.FindReferences(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenManyReferencesShareDeclarations_ThenTheyNestUnderTheirContainingTypeAndMember()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.References);
		var subject = new FindReferencesTool(registry, new SymbolResolver());

		string result = await subject.FindReferences(TestSolutions.References, "RefSpace.IThing");

		Assert.Contains("#resolvedSymbol=RefSpace.IThing", result);
		Assert.Contains("#count=12", result);
		Assert.Contains("#truncated=false", result);
		Assert.DoesNotContain("\r", result);

		Assert.Contains("RefLibrary/Things.cs", result);
		Assert.Contains("RefLibrary/Outer.cs", result);
		Assert.Contains("\tRefSpace\n", result);

		// A type referenced in its own base list carries a location; Alpha/Beta sit at namespace depth.
		Assert.Contains("\t\tclass,Alpha,", result);
		Assert.Contains("\t\tclass,Beta,", result);

		// Outer is referenced nowhere itself, so it is a parent-only line; Nested nests one level deeper.
		Assert.Contains("\t\tclass,Outer\n", result);
		Assert.Contains("\t\t\tclass,Nested,", result);

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
}
