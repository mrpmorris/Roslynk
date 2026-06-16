using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMembersTests;

public class GetMembersTests
{
	[Fact]
	public async Task WhenATypesMembersAreRequested_ThenItsPublicMethodsAreReturned()
	{
		string result = await RunAsync("SimpleLibrary.Greeter");

		Assert.Contains("#resolvedType=SimpleLibrary.Greeter", result);
		Assert.DoesNotContain("#error=", result);
		Assert.Contains("method,Greet", result);
	}

	[Fact]
	public async Task WhenAMetadataTypesMembersAreRequested_ThenTheyResolveUnderTheMetadataBucket()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("#resolvedType=System.String", result);
		Assert.Contains("<metadata>", result);
		Assert.Contains("method,Substring", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMembersTool(registry, new SymbolResolver());

		string result = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenANameFilterEndsWithAStar_ThenOnlyPrefixMatchesAreReturned()
	{
		string result = await RunAsync("System.String", nameFilter: "Sub*");

		Assert.Contains("method,Substring", result);
		Assert.All(MemberNames(result), name => Assert.StartsWith("Sub", name, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenANameFilterHasNoStar_ThenItMatchesAsACaseInsensitiveSubstring()
	{
		string result = await RunAsync("System.String", nameFilter: "ubSTR");

		Assert.Contains("method,Substring", result);
		Assert.All(MemberNames(result), name => Assert.Contains("ubstr", name, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenMethodsAreExcluded_ThenNoMethodsAreReturnedButOtherKindsRemain()
	{
		string result = await RunAsync("System.String", includeMethods: false);

		Assert.DoesNotContain("\tmethod,", result);
		Assert.Contains("property,Length", result);
	}

	[Fact]
	public async Task WhenOnlyFieldsAreRequested_ThenOtherKindsAreExcluded()
	{
		string result = await RunAsync(
			"System.String",
			includeMethods: false,
			includeProperties: false,
			includeEvents: false,
			includeNestedTypes: false);

		Assert.Contains("field,Empty", result);
		Assert.DoesNotContain("\tmethod,", result);
		Assert.DoesNotContain("\tproperty,", result);
	}

	[Fact]
	public async Task WhenANameFilterMatchesButItsKindIsExcluded_ThenItIsNotReturned()
	{
		string result = await RunAsync("System.String", nameFilter: "Length", includeProperties: false);

		// The Length property is gone; only the accessor method get_Length (a method) can still match.
		Assert.DoesNotContain("Length", MemberNames(result));
	}

	[Fact]
	public async Task WhenNoFiltersAreGiven_ThenMethodsAndPropertiesAreBothReturned()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("\tmethod,", result);
		Assert.Contains("\tproperty,", result);
	}

	[Fact]
	public async Task WhenAMemberHasSource_ThenItIsGroupedUnderASolutionRelativeFileWithALineRange()
	{
		string result = await RunAsync("SimpleLibrary.Greeter", nameFilter: "Greet");

		string fileLine = result.Split('\n').First(line => line.EndsWith("Greeter.cs", StringComparison.Ordinal));
		Assert.False(Path.IsPathRooted(fileLine), $"expected a solution-relative path, got '{fileLine}'");
		Assert.DoesNotContain('\\', fileLine);

		string memberLine = result.Split('\n').First(line => line.TrimStart('\t').StartsWith("method,Greet", StringComparison.Ordinal));
		// kind,name,<range> <signature>: the third comma-field is the line range.
		string range = memberLine.TrimStart('\t').Split(',')[2].Split(' ')[0];
		Assert.Matches(@"^\d+(-\d+)?$", range);
	}

	[Fact]
	public async Task WhenAMemberComesFromMetadata_ThenItHasNoFileOrLineRange()
	{
		string result = await RunAsync("System.String", nameFilter: "Substring");

		Assert.Contains("<metadata>", result);
		Assert.DoesNotContain(".cs", result);
		// No line range: kind,name is followed directly by the signature, not a third comma-field.
		Assert.Contains("method,Substring (", result);
	}

	private static async Task<string> RunAsync(
		string typeName,
		bool includePrivate = false,
		bool includeInherited = false,
		string? nameFilter = null,
		bool includeMethods = true,
		bool includeFields = true,
		bool includeProperties = true,
		bool includeEvents = true,
		bool includeNestedTypes = true)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		return await subject.GetMembers(
			TestSolutions.Simple,
			typeName,
			includePrivate,
			includeInherited,
			nameFilter,
			includeMethods,
			includeFields,
			includeProperties,
			includeEvents,
			includeNestedTypes);
	}

	private static IReadOnlyList<string> MemberNames(string text)
	{
		var names = new List<string>();
		foreach (string raw in text.Split('\n'))
		{
			if (!raw.StartsWith('\t'))
				continue;

			string line = raw.TrimStart('\t');
			int comma = line.IndexOf(',');
			if (comma < 0)
				continue;

			string rest = line[(comma + 1)..];
			int boundary = rest.IndexOfAny([',', ' ']);
			names.Add(boundary < 0 ? rest : rest[..boundary]);
		}

		return names;
	}
}
