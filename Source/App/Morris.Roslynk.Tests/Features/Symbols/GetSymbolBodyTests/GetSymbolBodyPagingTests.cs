using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetSymbolBodyTests;

/// <summary>
/// get_symbol_body's paging contract against the LargeType fixture: an oversized declaration arrives as a
/// page naming totalLines/nextStartLine, an oversized generated part arrives as a whole-or-omitted listing,
/// and a fitting declaration is byte-identical to the unpaged output. One loaded registry is shared by the
/// class (the fixture's Repro.Big is ~164KB; loading it per test would dominate the runtime).
/// </summary>
public class GetSymbolBodyPagingTests : IClassFixture<GetSymbolBodyPagingTests.LargeTypeFixture>
{
	private readonly LargeTypeFixture Fixture;

	public GetSymbolBodyPagingTests(LargeTypeFixture fixture)
	{
		Fixture = fixture;
	}

	[Fact]
	public async Task WhenABodyExceedsMaxLines_ThenGetSymbolBodyTruncatesAndReportsTheNextStartLine()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", maxLines: 400);

		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("totalLines=3003\n", result);
		Assert.Contains("nextStartLine=401\n", result);
		Assert.True(result.Length < 40_000); // 400 lines x ~53 chars + headers; the unpaged body is ~164K
		Assert.StartsWith("public class Big", RawBody(result));
		// Lines 1-400 of the declaration: the two class-header lines plus methods 0000-0397.
		Assert.Contains("public int Method0000(int value) => value + 0;", result);
		Assert.Contains("public int Method0397(int value) => value + 397;", result);
		Assert.DoesNotContain("Method0398", result);
	}

	[Fact]
	public async Task WhenMaxLinesIsBelowOne_ThenInvalidNamesTheParameter()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", maxLines: 0);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'maxLines'", result);
	}

	[Fact]
	public async Task WhenStartLineIsBelowOne_ThenInvalid()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: 0);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'startLine'", result);
	}

	[Fact]
	public async Task WhenStartLineIsBeyondTheBody_ThenInvalidNamesTheTotalLines()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: 4000);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("3003", result);
	}

	[Fact]
	public async Task WhenStartLineContinuesAPreviousPage_ThenNoLinesRepeatAndNoneAreSkipped()
	{
		string secondPage = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: 401, maxLines: 400);

		// Line 401 of the declaration is Method0398 (the class headers occupy lines 1-2).
		Assert.StartsWith("    public int Method0398(int value) => value + 398;", RawBody(secondPage));
		Assert.DoesNotContain("Method0397", secondPage);
	}

	[Fact]
	public async Task WhenPagesAreWalkedByNextStartLine_ThenTheyReassembleTheDeclarationByteExactly()
	{
		var subject = Subject();
		var pages = new List<string>();
		int startLine = 1;
		while (true)
		{
			string page = await subject.GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: startLine, maxLines: 400);
			pages.Add(RawBody(page));
			if (Header(page, "truncated") != "Y")
				break;
			startLine = int.Parse(Header(page, "nextStartLine"), System.Globalization.CultureInfo.InvariantCulture);
		}

		// Every page's body ends with one builder-added newline over its last whole line; stripping exactly
		// that and concatenating must reproduce the declaration verbatim - no line repeated, none skipped,
		// no CRLF torn in half.
		string reassembled = string.Concat(pages.Select(page => page[..^1]));
		Assert.Equal(TestSolutions.BigDeclaration, reassembled);
	}

	[Fact]
	public async Task WhenTheSourceUsesCrlf_ThenNoPageEndsWithACarriageReturn()
	{
		var subject = Subject();
		int startLine = 1;
		while (true)
		{
			string page = await subject.GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: startLine, maxLines: 400);
			string body = RawBody(page);

			// A body ends either with the builder's newline over a complete line (whose own CRLF precedes
			// it) or, on the last page, with the closing brace. A dangling '\r' would mean a torn CRLF.
			Assert.False(body.EndsWith("\r\n", StringComparison.Ordinal), $"Page at {startLine} ends with a torn carriage return.");

			if (Header(page, "truncated") != "Y")
				break;
			startLine = int.Parse(Header(page, "nextStartLine"), System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	[Fact]
	public async Task WhenThePageReachesTheEnd_ThenTruncatedAndNextStartLineAreAbsent()
	{
		// Lines 2604-3003 are exactly the last 400 lines, ending at the class's closing brace.
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.Big", startLine: 2604, maxLines: 400);

		Assert.DoesNotContain("truncated=", result);
		Assert.DoesNotContain("nextStartLine=", result);
		Assert.EndsWith("}", RawBody(result).TrimEnd('\n'));
	}

	[Fact]
	public async Task WhenTheBudgetEndsThePageBeforeMaxLines_ThenNextStartLineIsStillCorrect()
	{
		string result = await Fixture.CoreWithAllowance(10_000)
			.GetSymbolBodyCoreAsync(Fixture.Model, Fixture.Instance, "Repro.Big", maxLines: 3_000, budget: ResponseBudget.Allowance(10_000));

		Assert.True(result.Length <= 10_000, $"Result was {result.Length} chars; the allowance is 10,000.");
		Assert.Contains("truncated=Y\n", result);
		int nextStartLine = int.Parse(Header(result, "nextStartLine"), System.Globalization.CultureInfo.InvariantCulture);
		Assert.InRange(nextStartLine, 2, 3004);

		// The continuation starts exactly where the page stopped: no overlap, no gap.
		string lastMethod = RawBody(result).TrimEnd('\n')
			.Split('\n')
			.Select(line => line.TrimEnd('\r'))
			.Last(line => line.Contains("Method", StringComparison.Ordinal));
		int lastLineNumber = nextStartLine - 1;
		Assert.Equal($"    public int Method{lastLineNumber - 3:D4}(int value) => value + {lastLineNumber - 3};", lastMethod);
	}

	[Fact]
	public async Task WhenASingleLineExceedsTheWholeBudget_ThenPartialLineNamesIt()
	{
		string result = await Fixture.CoreWithAllowance(10_000)
			.GetSymbolBodyCoreAsync(Fixture.Model, Fixture.Instance, "Repro.LongLine.Value", budget: ResponseBudget.Allowance(10_000));

		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("totalLines=1\n", result);
		Assert.Contains("partialLine=1\n", result);
		Assert.True(result.Length <= 10_000);
		Assert.StartsWith("public const string Value = \"aaa", RawBody(result));
	}

	[Fact]
	public async Task WhenAGeneratedPartDoesNotFit_ThenTheOverviewOmitsItAndListsItsLineCount()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxLines: 10);

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
		string overview = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxLines: 10);
		string[] partLines = PartLines(overview);
		int partsCount = int.Parse(Header(overview, "parts"), System.Globalization.CultureInfo.InvariantCulture);
		Assert.Equal(partLines.Length, partsCount);
		(int omittedPart, _) = partLines
			.Select((line, index) => (index + 1, line))
			.First(pair => pair.line.EndsWith(",omitted=Y", StringComparison.Ordinal));
		Assert.True(omittedPart > 1, "The hand-written part should be shown whole before any generated part is omitted.");

		string part = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxLines: 10, part: omittedPart);

		Assert.StartsWith($"parts={partsCount}\n", part);
		Assert.Contains($"\npart={omittedPart}\n", part);
		Assert.Contains("generated=Y\n", part);
		Assert.Contains("truncated=Y\n", part); // the part is far larger than 10 lines
		Assert.Contains("nextStartLine=11\n", part);
	}

	[Fact]
	public async Task WhenPartIsBeyondTheParts_ThenInvalidNamesTheCount()
	{
		string overview = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxLines: 10);
		int partsCount = int.Parse(Header(overview, "parts"), System.Globalization.CultureInfo.InvariantCulture);

		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", part: partsCount + 1);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains($"part must be 0 to {partsCount}", result);
	}

	[Fact]
	public async Task WhenStartLineIsGivenForSeveralPartsWithoutPart_ThenInvalidAsksForPart()
	{
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", startLine: 2);

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
		// A raised maxLines keeps every part (the generated one is far larger than the default page size),
		// so the result is the complete listing with lines= counts appended to each part line.
		string result = await Subject().GetSymbolBody(TestSolutions.LargeType, "Repro.WideContext", maxLines: 5_000);

		string[] partLines = PartLines(result);
		Assert.Equal(partLines.Length, int.Parse(Header(result, "parts"), System.Globalization.CultureInfo.InvariantCulture));
		Assert.DoesNotContain("omitted=Y", result);
		Assert.DoesNotContain("\ntruncated=", result);
		Assert.Matches(@"^part=1,project=LargeTypeLib,path=LargeTypeLib/WideContext\.cs,loc=.*", partLines[0]);
		Assert.All(partLines, line => Assert.Matches(@",lines=\d+$", line));
	}

	private GetSymbolBodyTool Subject() => new(Fixture.Registry, new SymbolResolver(), new ProjectionService());

	private static string Body(string result)
	{
		string normalized = result.Replace("\r\n", "\n");
		int separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : normalized[(separator + 2)..];
	}

	private static string[] PartLines(string result) =>
		result.Split('\n').Where(line => line.StartsWith("part=", StringComparison.Ordinal)).ToArray();

	/// <summary>The raw body: everything after the headers' blank line, line endings untouched.</summary>
	private static string RawBody(string result)
	{
		int separator = result.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : result[(separator + 2)..];
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
			new(Registry, new SymbolResolver(), new ProjectionService(), ResponseBudget.Allowance(maxChars));
	}
}
