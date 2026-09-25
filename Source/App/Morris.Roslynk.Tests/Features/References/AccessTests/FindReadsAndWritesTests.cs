using Morris.Roslynk.Features.References.FindReads;
using Morris.Roslynk.Features.References.FindWrites;
using Morris.Roslynk.Infrastructure.Accesses;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.AccessTests;

public class FindReadsAndWritesTests
{
	private const string Mutate = "\t\t\t\t\tmethod,Mutate,";

	private static async Task<(FindReadsTool Reads, FindWritesTool Writes, InstanceRegistry Registry)> CreateAsync()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Access);
		return (
			new FindReadsTool(registry, new SymbolResolver(), new ProjectionService()),
			new FindWritesTool(registry, new SymbolResolver(), new ProjectionService()),
			registry);
	}

	private static string[] Lines(string result) => result.Split('\n');

	[Fact]
	public async Task WhenWritesToAFieldAreRequested_ThenEachWriteFormIsClassified()
	{
		(_, FindWritesTool writes, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await writes.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Total");

			Assert.Contains("resolvedSymbol=AccessSpace.Counter.Total", result);
			Assert.Contains("\t\t\t\t\tfield,Total,5:13,init", Lines(result));
			Assert.Contains("\t\t\t\t\tmethod,.ctor,11:3,init", Lines(result));
			Assert.Contains(Mutate + "18:3,assign", Lines(result));
			Assert.Contains(Mutate + "19:3,compound", Lines(result));
			Assert.Contains(Mutate + "20:3,increment", Lines(result));
			Assert.Contains(Mutate + "21:12,ref", Lines(result));
			Assert.Contains(Mutate + "22:11,out", Lines(result));
			Assert.Contains(Mutate + "23:4,assign", Lines(result));
			Assert.Contains(Mutate + "30:31,assign", Lines(result));
			Assert.DoesNotContain(",read", result);
			Assert.DoesNotContain("24:", result);
			Assert.DoesNotContain("25:", result);
			Assert.DoesNotContain("class,Other", result);
		}
	}

	[Fact]
	public async Task WhenReadsOfAFieldAreRequested_ThenDualAccessesAppearWithTheirOwnKind()
	{
		(FindReadsTool reads, _, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Total");

			Assert.Contains(Mutate + "17:14,read", Lines(result));
			Assert.Contains(Mutate + "19:3,compound", Lines(result));
			Assert.Contains(Mutate + "20:3,increment", Lines(result));
			Assert.Contains(Mutate + "21:12,ref", Lines(result));
			Assert.Contains(Mutate + "23:26,read", Lines(result));
			Assert.DoesNotContain(",assign", result);
			Assert.DoesNotContain(",out", result);
			Assert.DoesNotContain(",init", result);
			Assert.DoesNotContain("25:", result);
		}
	}

	[Fact]
	public async Task WhenAPropertyIsWrittenInItsConstructorAndAnInitialiser_ThenBothAreInit()
	{
		(_, FindWritesTool writes, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string count = await writes.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Count");
			string name = await writes.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Name");

			Assert.Contains("\t\t\t\t\tmethod,.ctor,12:8,init", Lines(count));
			Assert.Contains("\t\t\t\t\tmethod,Make,35:41,init", Lines(name));
		}
	}

	[Fact]
	public async Task WhenAParameterIsAddressedByMemberColonName_ThenOnlyItsAccessesAreReported()
	{
		(FindReadsTool reads, FindWritesTool writes, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string written = await writes.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Mutate:amount");
			string read = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Mutate(int, AccessSpace.Other):amount");
			string namedArgumentOnly = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Use:value");

			Assert.Contains("resolvedSymbol=AccessSpace.Counter.Mutate(int, Other):amount", written);
			Assert.Contains(Mutate + "26:3,assign", Lines(written));
			Assert.Contains(Mutate + "27:3,compound", Lines(written));
			Assert.Contains(Mutate + "18:11,read", Lines(read));
			Assert.Contains(Mutate + "28:7,read", Lines(read));
			Assert.DoesNotContain("29:", namedArgumentOnly);
			Assert.Contains("method,Use,33:36,read", namedArgumentOnly);
		}
	}

	[Fact]
	public async Task WhenALocalVariableIsRequested_ThenNotSupportedIsReturned()
	{
		(FindReadsTool reads, _, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Mutate:copy");

			Assert.Contains("error=NotSupported", result);
		}
	}

	[Fact]
	public async Task WhenTheSymbolCannotBeReadOrWritten_ThenNotSupportedIsReturned()
	{
		(FindReadsTool reads, _, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Make");

			Assert.Contains("error=NotSupported", result);
		}
	}

	[Fact]
	public async Task WhenTheParameterDoesNotExist_ThenNotFoundListsTheRealParameters()
	{
		(FindReadsTool reads, _, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Mutate:nope");

			Assert.Contains("error=NotFound", result);
			Assert.Contains("AccessSpace.Counter.Mutate(int, Other):amount", result);
		}
	}

	[Fact]
	public async Task WhenAnOverloadedOrMissingSymbolIsRequested_ThenTheResolverErrorsAreReturned()
	{
		(FindReadsTool reads, _, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			Assert.Contains("error=NotFound", await reads.FindReads(TestSolutions.Access, "AccessSpace.Counter.Missing"));
		}
	}

	[Fact]
	public async Task WhenMoreAccessesMatchThanMaxResults_ThenTheHeaderReportsTruncated()
	{
		(_, FindWritesTool writes, InstanceRegistry registry) = await CreateAsync();
		using (registry)
		{
			string result = await writes.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Total", maxResults: 2);

			Assert.Contains("truncated=Y", result);
			Assert.Contains("count=9", result);
			Assert.Equal(2, Lines(result).Count(line => line.EndsWith(",init") || line.EndsWith(",assign")));
		}
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenAnIndexingHeaderIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindWritesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindWrites(TestSolutions.Access, "AccessSpace.Counter.Total");

		Assert.Contains("error=Indexing", result);
		await registry.GetOrAddAsync(TestSolutions.Access);
	}

	[Theory]
	[InlineData("N.T.M:p", "N.T.M", "p")]
	[InlineData("N.T.M(int, string):p", "N.T.M(int, string)", "p")]
	[InlineData("N.T.M(global::X.Y):p", "N.T.M(global::X.Y)", "p")]
	[InlineData("N.T.F", "N.T.F", null)]
	[InlineData("N.T.M(global::X.Y)", "N.T.M(global::X.Y)", null)]
	public void WhenANameIsSplit_ThenTheParameterFollowsTheLastSingleColon(string input, string member, string? parameter)
	{
		Assert.Equal((member, parameter), AccessQuery.Split(input));
	}
}
