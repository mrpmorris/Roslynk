using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolBodyTests;

/// <summary>
/// get_symbol_body's re-contracted paging (issue 96): items are the text's lines, pages are bounded in
/// CHARACTERS by maxSize and the response budget (the old 400-line default cap is gone), headers are
/// count=/nextStartItem=/partialItem=, and an oversized single line is delivered as far as it fits and
/// named. The parts overview is ceiling-bounded only. One loaded registry is shared by the class (the
/// fixture's Repro.Big is ~164KB; loading it per test would dominate the runtime).
/// </summary>
public class GetSymbolBodyPagingTests : IClassFixture<GetSymbolBodyPagingTests.LargeTypeFixture>
{
	private readonly LargeTypeFixture Fixture;

	public GetSymbolBodyPagingTests(LargeTypeFixture fixture)
	{
		Fixture = fixture;
	}

	[Fact]
	public async Task WhenABodyExceedsMaxSize_ThenGetSymbolBodyPagesAndReportsTheNextStartItem()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", maxSize: 20_000);

		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("count=3003\n", result);
		Assert.Contains("nextStartItem=", result);
		Assert.True(result.Length <= 20_000, $"Page was {result.Length} chars against maxSize 20,000.");
		Assert.StartsWith("public class Big", RawBody(result));
		// Lines are packed by characters: roughly 370 of the 3,003 fit a 20,000-char page.
		Assert.Contains("public int Method0000(int value) => value + 0;", result);
		Assert.DoesNotContain("public int Method0400(", result);
	}

	[Fact]
	public async Task WhenNoMaxSizeIsGiven_ThenTheResultIsTheWholeDeclarationByteForByte()
	{
		// A generous budget: the whole declaration fits, so there is no paging and no 400-line default
		// cap - the result is the unpaged output itself, byte for byte.
		string result = await Fixture.CoreWithAllowance(500_000)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.Big");

		Assert.DoesNotContain("\ntruncated=", result);
		Assert.DoesNotContain("nextStartItem=", result);
		Assert.StartsWith("project=LargeTypeLib\n", result);
		int bodyStart = result.IndexOf("\n\n", StringComparison.Ordinal) + 2;
		Assert.Equal(TestSolutions.BigDeclaration + "\n", result[bodyStart..]);
	}

	[Fact]
	public async Task WhenMaxSizeIsBelowZero_ThenInvalidNamesTheParameter()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", maxSize: -1);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'maxSize'", result);
	}

	[Fact]
	public async Task WhenStartItemIsZero_ThenPageOneIsServed()
	{
		string zero = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: 0);
		string one = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: 1);

		Assert.DoesNotContain("error=", zero);
		Assert.Equal(one, zero);
	}

	[Fact]
	public async Task WhenStartItemIsNegative_ThenInvalid()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: -1);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'startItem'", result);
	}

	[Fact]
	public async Task WhenStartItemIsBeyondTheBody_ThenInvalidNamesTheCount()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: 4000);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'startItem' is past the end", result);
		Assert.Contains("3003", result);
	}

	[Fact]
	public async Task WhenStartItemContinuesAPreviousPage_ThenNoLinesRepeatAndNoneAreSkipped()
	{
		string firstPage = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", maxSize: 20_000);
		int nextStartItem = int.Parse(Header(firstPage, "nextStartItem"), System.Globalization.CultureInfo.InvariantCulture);
		string[] declarationLines = TestSolutions.BigDeclaration.Split('\n');

		string secondPage = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: nextStartItem, maxSize: 20_000);

		// The continuation starts exactly at the named item: the declaration's line nextStartItem, byte
		// for byte (its trailing CR included), and the predecessor line is nowhere on the page.
		Assert.Equal(declarationLines[nextStartItem - 1], RawBody(secondPage).Split('\n')[0]);
		Assert.DoesNotContain(declarationLines[nextStartItem - 2], secondPage);
	}

	[Fact]
	public async Task WhenPagesAreWalkedByNextStartItem_ThenTheyReassembleTheDeclarationByteExactly()
	{
		var subject = Subject();
		var pages = new List<string>();
		int startItem = 1;
		while (true)
		{
			string page = await subject.GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: startItem, maxSize: 40_000);
			pages.Add(RawBody(page));
			string? next = HeaderOrEmpty(page, "nextStartItem");
			if (next == "")
				break; // a page without nextStartItem= completes the walk
			startItem = int.Parse(next, System.Globalization.CultureInfo.InvariantCulture);
		}

		// Every page's body ends with the builder-added newline of its last whole line; concatenating
		// them whole reproduces the declaration plus exactly one trailing newline - no line repeated,
		// none skipped, no CRLF torn in half.
		string reassembled = string.Concat(pages)[..^1];
		Assert.Equal(TestSolutions.BigDeclaration, reassembled);
	}

	[Fact]
	public async Task WhenTheSourceUsesCrlf_ThenNoPageEndsWithATornCarriageReturn()
	{
		var subject = Subject();
		int startItem = 1;
		while (true)
		{
			string page = await subject.GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: startItem, maxSize: 40_000);
			string body = RawBody(page);

			// A page's lines keep their CR (the source's CRLF minus the LF the builder writes), so a
			// mid-file page ends "\r\n". A dangling '\r' with no newline would mean a torn pair.
			Assert.False(body.EndsWith("\r", StringComparison.Ordinal), $"Page at {startItem} ends with a torn carriage return.");

			string? next = HeaderOrEmpty(page, "nextStartItem");
			if (next == "")
				break; // a page without nextStartItem= completes the walk
			startItem = int.Parse(next, System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	[Fact]
	public async Task WhenThePageReachesTheEnd_ThenNextStartItemIsAbsent()
	{
		// Lines 2604-3003 are exactly the last 400 lines, ending at the class's closing brace.
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startItem: 2604, maxSize: 40_000);

		// The completing page names the set but drops neither truncated= nor nextStartItem=: nothing
		// remains to fetch, and truncated= only ever means "more retrievable content".
		Assert.Contains("count=3003\n", result);
		Assert.DoesNotContain("truncated=", result);
		Assert.DoesNotContain("nextStartItem=", result);
		Assert.EndsWith("}", RawBody(result).TrimEnd('\n'));
	}

	[Fact]
	public async Task WhenTheBudgetEndsThePageBeforeMaxSize_ThenNextStartItemIsStillCorrect()
	{
		// No maxSize: the page is char-bounded by the tool's budget alone.
		string result = await Fixture.CoreWithAllowance(10_000)
			.GetSymbolBodyCoreAsync(Fixture.Model, Fixture.Instance, "Repro.Big", budget: new ResponseBudget(10_000));

		Assert.True(result.Length <= 10_000, $"Result was {result.Length} chars; the budget is 10,000.");
		Assert.Contains("truncated=Y\n", result);
		int nextStartItem = int.Parse(Header(result, "nextStartItem"), System.Globalization.CultureInfo.InvariantCulture);
		Assert.InRange(nextStartItem, 2, 3004);

		// The continuation starts exactly where the page stopped: no overlap, no gap.
		string lastMethod = RawBody(result).TrimEnd('\n')
			.Split('\n')
			.Select(line => line.TrimEnd('\r'))
			.Last(line => line.Contains("Method", StringComparison.Ordinal));
		int lastLineNumber = nextStartItem - 1;
		Assert.Equal($"    public int Method{lastLineNumber - 3:D4}(int value) => value + {lastLineNumber - 3};", lastMethod);
	}

	[Fact]
	public async Task WhenASingleLineExceedsTheWholeBudget_ThenPartialItemNamesIt()
	{
		string result = await Fixture.CoreWithAllowance(10_000)
			.GetSymbolBodyCoreAsync(Fixture.Model, Fixture.Instance, "Repro.LongLine.Value", budget: new ResponseBudget(10_000));

		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("count=1\n", result);
		Assert.Contains("partialItem=1\n", result);
		Assert.DoesNotContain("nextStartItem=", result);
		Assert.True(result.Length <= 10_000);
		Assert.StartsWith("public const string Value = \"aaa", RawBody(result));
	}

	[Fact]
	public async Task WhenAGeneratedPartDoesNotFit_ThenTheOverviewOmitsItAndListsItsLineCount()
	{
		// The overview triggers on the ceiling alone; a 1,500-char budget cannot carry the generated part.
		string result = await Fixture.CoreWithAllowance(1_500)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext");

		Assert.Contains("parts=", result);
		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("omittedParts=", result);

		string[] partLines = PartLines(result);
		Assert.True(partLines.Length >= 2, $"Expected at least two parts: {result}");
		Assert.Contains(partLines, line => line.EndsWith(",omitted=Y", StringComparison.Ordinal) && line.Contains("generated=Y", StringComparison.Ordinal));
		Assert.All(partLines, line => Assert.Contains(",lines=", line));
		// The hand-written part is shown whole; the omitted part's text is nowhere in the result.
		Assert.Contains("JsonSerializerContext", result);
	}

	[Fact]
	public async Task WhenAnOmittedPartIsRequested_ThenOnlyThatPartIsReturnedAndPaged()
	{
		// The overview is parsed, not pinned: generator part shapes and counts are SDK-dependent.
		string overview = await Fixture.CoreWithAllowance(1_500)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext");
		string[] partLines = PartLines(overview);
		int partsCount = int.Parse(Header(overview, "parts"), System.Globalization.CultureInfo.InvariantCulture);
		Assert.Equal(partLines.Length, partsCount);
		(int omittedPart, _) = partLines
			.Select((line, index) => (index + 1, line))
			.First(pair => pair.line.EndsWith(",omitted=Y", StringComparison.Ordinal));
		Assert.True(omittedPart > 1, "The hand-written part should be shown whole before any generated part is omitted.");

		string part = await Fixture.CoreWithAllowance(10_000)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxSize: 500, part: omittedPart);

		Assert.StartsWith($"parts={partsCount}\n", part);
		Assert.Contains($"\npart={omittedPart}\n", part);
		Assert.Contains("generated=Y\n", part);
		Assert.Contains("truncated=Y\n", part); // the part is far larger than 500 characters
		Assert.NotEqual("", Header(part, "nextStartItem"));
	}

	[Fact]
	public async Task WhenPartIsBeyondTheParts_ThenInvalidNamesTheCount()
	{
		string overview = await Fixture.CoreWithAllowance(1_500)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext");
		int partsCount = int.Parse(Header(overview, "parts"), System.Globalization.CultureInfo.InvariantCulture);

		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", part: partsCount + 1);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains($"part must be 0 to {partsCount}", result);
	}

	[Fact]
	public async Task WhenStartItemIsGivenForSeveralPartsWithoutPart_ThenInvalidAsksForPart()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", startItem: 2);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("pass part=<n>", result);
	}

	[Fact]
	public async Task WhenPartOneIsAskedForASinglePartSymbol_ThenTodaySBytesAreReturned()
	{
		string plain = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Wide");
		string partOne = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Wide", part: 1);

		Assert.Equal(plain, partOne);
		Assert.DoesNotContain("part=", partOne);
	}

	[Fact]
	public async Task WhenEveryPartFits_ThenEachPartLineCarriesItsLineCount()
	{
		// A generous ceiling keeps every part (the generated one would otherwise be omitted), so the
			// result is the complete listing with lines= counts appended to each part line.
		string result = await Fixture.CoreWithAllowance(500_000)
			.GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext");

		string[] partLines = PartLines(result);
		Assert.Equal(partLines.Length, int.Parse(Header(result, "parts"), System.Globalization.CultureInfo.InvariantCulture));
		Assert.DoesNotContain("omitted=Y", result);
		Assert.DoesNotContain("\ntruncated=", result);
		Assert.Matches(@"^part=1,project=LargeTypeLib,path=LargeTypeLib/WideContext\.cs,loc=.*", partLines[0]);
		Assert.All(partLines, line => Assert.Matches(@",lines=\d+$", line));
	}

	private GetSymbolBodyTool Subject() => new(Fixture.Registry, new SymbolResolver(), new ProjectionService());

	private static string[] PartLines(string result) =>
		result.Split('\n').Where(line => line.StartsWith("part=", StringComparison.Ordinal)).ToArray();

	/// <summary>The raw body: everything after the headers' blank line, line endings untouched.</summary>
	private static string RawBody(string result)
	{
		int separator = result.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : result[(separator + 2)..];
	}

	private static string HeaderOrEmpty(string result, string key)
	{
		string prefix = key + "=";
		foreach (string line in result.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.StartsWith(prefix, StringComparison.Ordinal))
				return line[prefix.Length..];
		}

		return "";
	}

	private static string Header(string result, string key)
	{
		foreach (string line in result.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.StartsWith(key + "=", StringComparison.Ordinal))
				return line[(key.Length + 1)..];
		}

		return "";
	}

	public sealed class LargeTypeFixture : IAsyncLifetime, IDisposable
	{
		public InstanceRegistry Registry { get; } = new();

		public RoslynInstance Instance { get; private set; } = null!;

		public SolutionModel Model { get; private set; } = null!;

		public async Task InitializeAsync()
		{
			await Registry.GetOrAddAsync(TestSolutions.LargeType);
			Instance = await Registry.GetOrBeginAsync(TestSolutions.LargeType);
			Model = await Instance.ReadModelAsync();
		}

		public Task DisposeAsync() => Task.CompletedTask;

		public void Dispose() => Registry.Dispose();

		/// <summary>A tool whose budget is the given per-result character ceiling, for core-level tests.</summary>
		public GetSymbolBodyTool CoreWithAllowance(int maxChars) =>
			new(Registry, new SymbolResolver(), new ProjectionService(), new ResponseBudget(maxChars));
	}
}
