using System.Globalization;
using System.Text.RegularExpressions;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

/// <summary>
/// Issue 96's paging scenarios against find_references on the References fixture (RefSpace.IThing is
/// referenced across several files, types and members): page 1 of a fitting result is the unpaged
/// output byte for byte; a continuation page repeats its items' container chain and covers every item;
/// a maxSize that cannot carry one item beside its chain is Invalid naming the count; and maxResults
/// bounds the SET that paging then walks, with count= naming the capped size.
/// </summary>
public class FindReferencesPagingTests : IClassFixture<FindReferencesPagingTests.ReferencesFixture>
{
	private const string SymbolName = "RefSpace.IThing";

	private readonly ReferencesFixture Fixture;

	public FindReferencesPagingTests(ReferencesFixture fixture)
	{
		Fixture = fixture;
	}

	[Fact]
	public async Task WhenEveryReferenceFits_ThenTheResultIsTheUnpagedOutputWithNoPagingHeaders()
	{
		var subject = Subject();

		string whole = await subject.FindReferences(TestSolutions.References, SymbolName);
		string explicitFit = await subject.FindReferences(TestSolutions.References, SymbolName, maxSize: 1_000_000);

		Assert.StartsWith("resolvedSymbol=RefSpace.IThing\n", whole);
		Assert.DoesNotContain("\ntruncated=", whole);
		Assert.DoesNotContain("count=", whole);
		Assert.DoesNotContain("nextStartItem=", whole);
		Assert.Equal(whole, explicitFit); // an explicit maxSize that fits everything changes no byte
	}

	[Fact]
	public async Task WhenOnlySomeItemsFit_ThenContinuationPagesRepeatTheContainersAndCoverEveryItem()
	{
		var subject = Subject();
		string whole = await subject.FindReferences(TestSolutions.References, SymbolName);
		string[] wholeLines = BodyLines(whole);
		var wholeLeaves = LeafLines(whole).ToHashSet();

		// A third of the whole render (floored so a chain + item always fits beside the header reserve)
		// forces several pages.
		int maxSize = Math.Max(260, whole.Length / 3);

		var pages = new List<string>();
		int startItem = 1;
		string? expectedCount = null;
		while (true)
		{
			string page = await subject.FindReferences(TestSolutions.References, SymbolName, startItem: startItem, maxSize: maxSize);
			pages.Add(page);

			// count= is constant across pages and names the items - never the preamble lines a page repeats.
			expectedCount ??= Header(page, "count");
			Assert.Equal(expectedCount, Header(page, "count"));
			Assert.Equal(wholeLeaves.Count.ToString(CultureInfo.InvariantCulture), expectedCount);
			Assert.True(page.Length <= maxSize, $"Page was {page.Length} chars against maxSize {maxSize}.");

			// Every page is a self-contained subsequence of the whole render's lines: nothing fabricated.
			AssertIsSubsequence(BodyLines(page), wholeLines);

			string? next = HeaderOrEmpty(page, "nextStartItem");
			if (next == "")
				break; // the completing page carries no truncated= and no nextStartItem=
			Assert.Equal("Y", Header(page, "truncated")); // every page with a successor was truncated
			startItem = int.Parse(next, CultureInfo.InvariantCulture);
		}

		Assert.True(pages.Count > 1, "The result must take several pages under the chosen maxSize.");

		// Every reference lands on some page, exactly once: the union of the pages' leaf lines is the
			// whole render's leaf lines.
		var covered = pages.SelectMany(LeafLines).ToHashSet();
		Assert.Equal(wholeLeaves, covered);

		// A continuation page opens with its first item's container chain - lines that are containers in
			// the whole render, not the previous page's last leaf.
		string[] continuationLines = Body(pages[1]).Split('\n');
		Assert.Contains(continuationLines, line => !LeafRegex.IsMatch(line) && wholeLines.Contains(line));
	}

	[Fact]
	public async Task WhenMaxSizeCannotCarryOneItemBesideItsChain_ThenInvalidNamesTheNeededCharacters()
	{
		string result = await Subject().FindReferences(TestSolutions.References, SymbolName, maxSize: 100);

		Assert.StartsWith("error=Invalid", result);
		Assert.Contains("'maxSize' is too small", result);
		Assert.Matches(@"(needs \d+ characters|context alone is \d+ characters)", result);
	}

	[Fact]
	public async Task WhenMaxResultsCapsTheSet_ThenCountNamesTheCappedSizeAndNoNextStartItemIsEmitted()
	{
		string result = await Subject().FindReferences(TestSolutions.References, SymbolName, maxResults: 1);

		Assert.Contains("truncated=Y\n", result);
		Assert.Contains("count=1\n", result);
		Assert.DoesNotContain("nextStartItem=", result); // nextStartItem= is THE paging discriminator
		Assert.Single(LeafLines(result)); // the set is capped, not the page
	}

	[Fact]
	public async Task WhenMaxResultsIsOmitted_ThenCountNamesEveryItemAndPagingWalksThemAll()
	{
		var subject = Subject();
		string whole = await subject.FindReferences(TestSolutions.References, SymbolName);
		int total = LeafLines(whole).Length;

		string page = await subject.FindReferences(TestSolutions.References, SymbolName, maxSize: Math.Max(260, whole.Length / 3));

		Assert.Equal(total.ToString(CultureInfo.InvariantCulture), Header(page, "count")); // uncapped total
		Assert.True(int.Parse(Header(page, "nextStartItem"), CultureInfo.InvariantCulture) > 1);
	}

	[Fact]
	public async Task WhenMaxResultsBoundsTheSet_ThenPagingWalksOnlyTheCappedSet()
	{
		var subject = Subject();
		string whole = await subject.FindReferences(TestSolutions.References, SymbolName);
		var referenceSet = LeafLines(whole).ToHashSet();

		var seen = new List<string>();
		int startItem = 1;
		while (true)
		{
			string page = await subject.FindReferences(TestSolutions.References, SymbolName, maxResults: 2, startItem: startItem, maxSize: Math.Max(260, whole.Length / 3));
			Assert.Equal("2", Header(page, "count")); // count= names the CAPPED size, not the total available
			seen.AddRange(LeafLines(page));

			string? next = HeaderOrEmpty(page, "nextStartItem");
			if (next == "")
				break; // the completing page carries no truncated= and no nextStartItem=
			Assert.Equal("Y", Header(page, "truncated")); // every page with a successor was truncated
			startItem = int.Parse(next, CultureInfo.InvariantCulture);
		}

		// The walk covered the capped set exactly: two leaves, every one a real reference of the symbol.
		Assert.Equal(2, seen.Count);
		Assert.True(seen.All(referenceSet.Contains), "The walked pages delivered a reference outside the symbol's set.");
	}

	private FindReferencesTool Subject() => new(Fixture.Registry, new SymbolResolver(), new ProjectionService());

	private static readonly Regex LeafRegex = new(@",\d+:\d+", RegexOptions.Compiled);

	private static string[] BodyLines(string result) => Body(result).Split('\n');

	/// <summary>The lines that carry a location - the leaf records, as opposed to container lines.</summary>
	private static string[] LeafLines(string result) =>
		BodyLines(result).Where(line => LeafRegex.IsMatch(line)).ToArray();

	private static string Body(string result)
	{
		int separator = result.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : result[(separator + 2)..];
	}

	private static string Header(string result, string key)
	{
		string header = HeaderOrEmpty(result, key);
		Assert.True(header != "", $"Result lacked a '{key}=' header: {result[..Math.Min(300, result.Length)]}");
		return header;
	}

	private static string HeaderOrEmpty(string result, string key)
	{
		string prefix = key + "=";
		foreach (string line in result.Split('\n'))
		{
			if (line.StartsWith(prefix, StringComparison.Ordinal))
				return line[prefix.Length..].TrimEnd('\r');
		}

		return "";
	}

	private static void AssertIsSubsequence(string[] subset, string[] whole)
	{
		int at = 0;
		foreach (string line in subset)
		{
			while (at < whole.Length && whole[at] != line)
				at++;
			Assert.True(at < whole.Length, $"Page line '{line}' is not part of the whole render's lines in order.");
			at++;
		}
	}

	public sealed class ReferencesFixture : IAsyncLifetime, IDisposable
	{
		public InstanceRegistry Registry { get; } = new();

		public async Task InitializeAsync() => await Registry.GetOrAddAsync(TestSolutions.References);

		public Task DisposeAsync() => Task.CompletedTask;

		public void Dispose() => Registry.Dispose();
	}
}
