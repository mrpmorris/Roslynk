using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMembersTests;

public class GetMembersTests
{
	[Fact]
	public async Task WhenATypesMembersAreRequested_ThenItsPublicMethodsAreReturned()
	{
		string result = await RunAsync("SimpleLibrary.Greeter");

		Assert.Contains("resolvedType=SimpleLibrary.Greeter", result);
		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Greet", result);
	}

	[Fact]
	public async Task WhenATypeHasPrivateMembers_ThenTheyAreIncluded()
	{
		string result = await RunAsync("SimpleLibrary.Holder");

		Assert.Contains("field,_ready", result);
	}

	[Fact]
	public async Task WhenAMetadataTypesMembersAreRequested_ThenTheyResolveUnderTheMetadataBucket()
	{
		string result = await RunAsync("System.String");

		Assert.Contains("resolvedType=System.String", result);
		Assert.Contains("<metadata>", result);
		Assert.Contains("method,Substring", result);
	}

	[Fact]
	public async Task WhenTheLoadWaitTimeoutElapses_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

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
	public async Task WhenAMemberHasSource_ThenItIsGroupedUnderASolutionRelativeFileWithItsLoc()
	{
		string result = await RunAsync("SimpleLibrary.Greeter", nameFilter: "Greet");

		Assert.Contains(result.Split('\n'), line => line == "SimpleLibrary");

		string fileLine = result.Split('\n').First(line => line.EndsWith("Greeter.cs", StringComparison.Ordinal));
		Assert.False(Path.IsPathRooted(fileLine), $"expected a solution-relative path, got '{fileLine}'");
		Assert.DoesNotContain('\\', fileLine);

		string memberLine = result.Split('\n').First(line => line.TrimStart('\t').StartsWith("method,Greet", StringComparison.Ordinal));
		// kind,name,<loc>,<signature>: the third comma-field is the start:col-end:col span.
		string loc = memberLine.TrimStart('\t').Split(',')[2];
		Assert.Matches(@"^\d+:\d+-\d+:\d+$", loc);
	}

	[Fact]
	public async Task WhenAMemberComesFromMetadata_ThenItsLocFieldIsEmpty()
	{
		string result = await RunAsync("System.String", nameFilter: "Substring");

		Assert.Contains("<metadata>", result);
		Assert.DoesNotContain(".cs", result);
		// Empty loc field: kind,name,,<signature> (the doubled comma is the explicitly empty loc).
		Assert.Contains("method,Substring,,", result);
	}

	[Fact]
	public async Task WhenAMethodHasParameters_ThenTheSignatureIsAPipeDelimitedTypeList()
	{
		string result = await RunAsync("SimpleLibrary.Holder", nameFilter: "Combine");

		string memberLine = result.Split('\n').First(line => line.TrimStart('\t').StartsWith("method,Combine", StringComparison.Ordinal));
		// kind,name,<loc>,<paramType|paramType>
		string[] fields = memberLine.TrimStart('\t').Split(',');
		Assert.Equal("string|string", fields[3]);
	}

	[Fact]
	public async Task WhenAMetadataTypeIsListedWithoutProperties_ThenNoAccessorMethodsAreReturned()
	{
		string result = await RunAsync(
			"System.String",
			includeProperties: false,
			includeEvents: false,
			includeFields: false,
			includeNestedTypes: false);

		Assert.Contains("method,Substring", result);
		// Constructors are ordinary methods for this flag and must survive the accessor exclusion.
		Assert.Contains("method,.ctor,", result);
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,get_", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,set_", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenASourceTypeHasAProperty_ThenItsLeafRepresentsItAndNoAccessorMethodsAreReturned()
	{
		string result = await RunAsync("SimpleLibrary.Holder");

		Assert.Contains("property,Count,", result);
		Assert.Contains("method,Combine,", result);
		Assert.DoesNotContain("\tmethod,get_Count,", result);
		Assert.DoesNotContain("\tmethod,set_Count,", result);
	}

	[Fact]
	public async Task WhenMethodsAreIncludedAndPropertiesAreExcluded_ThenOnlyOrdinaryMethodsRemain()
	{
		string result = await RunAsync("SimpleLibrary.Holder", includeProperties: false);

		Assert.Contains("\tmethod,Combine,", result);
		Assert.DoesNotContain("\tproperty,", result);
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,get_", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,set_", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenASourceTypeHasExplicitPropertyAndEventAccessors_ThenOnlyTheirLeavesAreReturned()
	{
		string solution = TestSolutions.CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(solution)!, "SimpleLibrary");
		await File.WriteAllTextAsync(Path.Combine(library, "AccessorProbe.cs"), """
			namespace SimpleLibrary;

			public class AccessorProbe
			{
				public AccessorProbe(int value)
				{
					_value = value;
				}

				public int Value
				{
					get => _value;
					set => _value = value;
				}

				public event System.EventHandler? Changed;

				private int _value;

				public void Raise() => Changed?.Invoke(this, System.EventArgs.Empty);
			}
			""");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solution);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(solution, "SimpleLibrary.AccessorProbe");

		Assert.Contains("property,Value,", result);
		Assert.Contains("event,Changed,", result);
		Assert.Contains("\tmethod,.ctor,", result);
		Assert.Contains("\tmethod,Raise,", result);
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,get_Value", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,set_Value", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,add_Changed", StringComparison.Ordinal));
		Assert.DoesNotContain(result.Split('\n'), line => line.TrimStart('\t').StartsWith("method,remove_Changed", StringComparison.Ordinal));
	}

	private static async Task<string> RunAsync(
		string symbolName,
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
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetMembers(
			TestSolutions.Simple,
			symbolName,
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
