using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

/// <summary>
/// The shared item pager (issue 96): a page carries whole items plus each item's container chain, the
/// first page of a fitting result is byte-identical to the unpaged output, and the headers name what a
/// continuation needs. Costs are exact because the pager charges depth + text + 1 per line; the header
/// reserve helper mirrors that documented reserve so maxSize boundaries are precise.
/// </summary>
public class ItemPagingTests
{
	/// <summary>The widest header block the pager reserves before packing (no tool headers, Ready status).</summary>
	private static int HeaderReserve(int itemCount) =>
		1 // the blank line between headers and body
		+ "truncated=Y\n".Length
		+ $"count={itemCount}\n".Length
		+ $"nextStartItem={itemCount + 1}\n".Length
		+ $"partialItem={itemCount}\n".Length;

	/// <summary>The pager's line cost: depth tabs, the text, the newline.</summary>
	private static int Cost(int depth, string text) => depth + text.Length + 1;

	[Fact]
	public void WhenPageOneCoversEveryItem_ThenTheOutputIsByteIdenticalToTheWholeRender()
	{
		var items = new[]
		{
			OutlineItem.Single("l1", ["A"]),
			OutlineItem.Single("l2", ["A"]),
			OutlineItem.Single("l3", ["B"]),
		};
		const string whole = "A\n\tl1\n\tl2\nB\n\tl3\n";

		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "A");
			builder.Line(1, "l1");
			builder.Line(1, "l2");
			builder.Line(0, "B");
			builder.Line(1, "l3");
		}

		// No budget and no explicit paging: the unpaged output itself.
		PagedRender plain = ItemPaging.Render(SolutionStatus.Ready, _ => { }, RenderBody, items);
		// An explicit maxSize that fits everything is still the whole render, byte for byte.
		PagedRender explicitFit = ItemPaging.Render(
			SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 1, maxSize: 500, budget: new ResponseBudget(80_000));

		Assert.Equal(whole, Assert.IsType<PagedRender.Complete>(plain).Output);
		Assert.Equal(whole, Assert.IsType<PagedRender.Complete>(explicitFit).Output);
		Assert.DoesNotContain("truncated=", Assert.IsType<PagedRender.Complete>(explicitFit).Output);
	}

	[Fact]
	public void WhenAnItemsLineIsItsSuccessorsContainer_ThenTheSharedLineIsEmittedOnce()
	{
		var items = new[]
		{
			OutlineItem.Single("class,C", []),
			OutlineItem.Single("memberXXXXXXXXXXXX", ["class,C"]),
			OutlineItem.Single("memberYYYYYYYYYYYY", ["class,C"]),
			OutlineItem.Single("memberZZZZZZZZZZZZ", ["class,C"]),
			OutlineItem.Single("memberWWWWWWWWWWWW", ["class,C"]),
			OutlineItem.Single("memberVVVVVVVVVVVV", ["class,C"]),
		};

		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "class,C");
			builder.Line(1, "memberXXXXXXXXXXXX");
			builder.Line(1, "memberYYYYYYYYYYYY");
			builder.Line(1, "memberZZZZZZZZZZZZ");
			builder.Line(1, "memberWWWWWWWWWWWW");
			builder.Line(1, "memberVVVVVVVVVVVV");
		}

		// bodyBudget = 95 - HeaderReserve(6) = 44: the type line (8) and one member (20) pack, the next does not.
		PagedRender pageOne = ItemPaging.Render(
			SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 1, maxSize: 95);
		string bodyOne = RawBody(Assert.IsType<PagedRender.Page>(pageOne).Output);

		Assert.Equal("class,C\n\tmemberXXXXXXXXXXXX\n", bodyOne);
		Assert.Equal(1, CountOccurrences(bodyOne, "class,C")); // the line is written once, not repeated

		PagedRender pageTwo = ItemPaging.Render(
			SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 3, maxSize: 95);
		string bodyTwo = RawBody(Assert.IsType<PagedRender.Page>(pageTwo).Output);

		// The continuation is self-contained: the container chain is re-emitted ahead of its item.
		Assert.Equal("class,C\n\tmemberYYYYYYYYYYYY\n", bodyTwo);
	}

	[Fact]
	public void WhenContinuationPagesRepeatTheChain_ThenConcatenatingThemMinusTheRepeatsReproducesTheWhole()
	{
		var items = Enumerable.Range(1, 9)
			.Select(index => OutlineItem.Single($"leaf{index}", ["A", "B"]))
			.ToArray();

		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "A");
			builder.Line(1, "B");
			for (int index = 1; index <= 9; index++)
				builder.Line(2, $"leaf{index}");
		}

		const string whole = "A\n\tB\n\t\tleaf1\n\t\tleaf2\n\t\tleaf3\n\t\tleaf4\n\t\tleaf5\n\t\tleaf6\n\t\tleaf7\n\t\tleaf8\n\t\tleaf9\n";
		const int maxSize = 66; // HeaderReserve(9) = 52, so the body budget is 14: chain (5) + one leaf (8), no more.

		var pages = new List<string>();
		for (int startItem = 1; startItem <= 9; startItem++)
		{
			PagedRender page = ItemPaging.Render(
				SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: startItem, maxSize: maxSize);
			pages.Add(Assert.IsType<PagedRender.Page>(page).Output);

			// count= names the items, never the preamble lines the page repeats; and the page - chain
			// included - stays within maxSize.
			Assert.Equal("9", Header(pages[^1], "count"));
			Assert.True(pages[^1].Length <= maxSize, $"Page {startItem} was {pages[^1].Length} chars against maxSize {maxSize}.");
			Assert.EndsWith("\t\tleaf" + startItem + "\n", RawBody(pages[^1]));
		}

		// The last page has nothing left to continue to.
		Assert.DoesNotContain("nextStartItem=", pages[^1]);
		for (int index = 0; index < 8; index++)
			Assert.Equal($"{index + 2}", Header(pages[index], "nextStartItem"));

		// Dropping each continuation's repeated container chain and concatenating rebuilds the whole render.
		string reassembled = RawBody(pages[0]) + string.Concat(pages.Skip(1).Select(page => RawBody(page)["A\n\tB\n".Length..]));
		Assert.Equal(whole, reassembled);
	}

	[Fact]
	public void WhenStartItemIsPastTheEnd_ThenInvalidNamesTheItemCount()
	{
		var items = new[] { OutlineItem.Single("l1", []), OutlineItem.Single("l2", []), OutlineItem.Single("l3", []) };

		PagedRender render = ItemPaging.Render(SolutionStatus.Ready, _ => { }, _ => { }, items, startItem: 4);

		Assert.Equal("'startItem' is past the end: the result has 3 items.", Assert.IsType<PagedRender.Invalid>(render).Message);
	}

	[Fact]
	public void WhenMaxSizeIsNegative_ThenInvalidNamesTheParameter()
	{
		PagedRender render = ItemPaging.Render(SolutionStatus.Ready, _ => { }, _ => { }, [], startItem: 1, maxSize: -1);

		Assert.Equal("'maxSize' must be at least 0 (got -1).", Assert.IsType<PagedRender.Invalid>(render).Message);
	}

	[Fact]
	public void WhenStartItemIsZero_ThenPageOneIsServed()
	{
		var items = new[] { OutlineItem.Single("l1", []), OutlineItem.Single("l2", []) };
		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "l1");
			builder.Line(0, "l2");
		}

		PagedRender zero = ItemPaging.Render(SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 0);
		PagedRender one = ItemPaging.Render(SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 1);

		Assert.Equal(Assert.IsType<PagedRender.Complete>(one).Output, Assert.IsType<PagedRender.Complete>(zero).Output);
	}

	[Fact]
	public void WhenStartItemIsNegative_ThenInvalidNamesTheParameter()
	{
		PagedRender render = ItemPaging.Render(SolutionStatus.Ready, _ => { }, _ => { }, [], startItem: -1);

		Assert.Equal("'startItem' counts items from 1 (got -1).", Assert.IsType<PagedRender.Invalid>(render).Message);
	}

	[Fact]
	public void WhenASingleLineItemExceedsTheWholePage_ThenItIsCutAndNamedPartialItem()
	{
		var items = new[] { OutlineItem.Single(new string('x', 100), []) };
		const int maxSize = 60;

		PagedRender render = ItemPaging.Render(SolutionStatus.Ready, _ => { }, builder => builder.Line(0, new string('x', 100)), items, startItem: 1, maxSize: maxSize);
		string page = Assert.IsType<PagedRender.Page>(render).Output;

		Assert.Equal("1", Header(page, "partialItem"));
		Assert.Equal("1", Header(page, "count"));
		Assert.DoesNotContain("nextStartItem=", page); // the rest of the line is not retrievable by paging
		int bodyBudget = maxSize - HeaderReserve(1);
		Assert.Equal(new string('x', bodyBudget - 1) + "\n", RawBody(page));
		Assert.True(page.Length <= maxSize, $"Page was {page.Length} chars against maxSize {maxSize}.");
	}

	[Fact]
	public void WhenMaxSizeCannotCarryAnItemBesideItsChain_ThenInvalidNamesTheNeededCharacters()
	{
		var items = Enumerable.Range(1, 8)
			.Select(index => OutlineItem.Single($"leaf{index}", ["A", "B"]))
			.ToArray();

		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "A");
			builder.Line(1, "B");
			for (int index = 1; index <= 8; index++)
				builder.Line(2, $"leaf{index}");
		}

		// bodyBudget = 62 - HeaderReserve(8) = 11: an item (8) would fit a fresh page but not beside its chain (5).
		PagedRender render = ItemPaging.Render(
			SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 2, maxSize: 62);

		int needed = Cost(0, "A") + Cost(1, "B") + Cost(2, "leaf2");
		int itemCost = Cost(2, "leaf2");
		PagedRender.Invalid invalid = Assert.IsType<PagedRender.Invalid>(render);
		Assert.Contains($"'maxSize' is too small: item 2 needs {needed} characters including its context lines.", invalid.Message);
		Assert.Contains($"The item itself is {itemCost} characters", invalid.Message);
		Assert.Contains("raise maxSize to at least", invalid.Message);
	}

	[Fact]
	public void WhenAnItemCarriesSeveralLinesAtDepths_ThenTheyRenderBelowTheChain()
	{
		var member = new OutlineItem([(0, "member,M"), (1, "localFunction,F")], ["class,C"]);
		var items = new[] { member, OutlineItem.Single("member,N", ["class,C"]) };

		static void RenderBody(OutlineBuilder builder)
		{
			builder.Line(0, "class,C");
			builder.Line(1, "member,M");
			builder.Line(2, "localFunction,F");
			builder.Line(1, "member,N");
		}

		PagedRender whole = ItemPaging.Render(SolutionStatus.Ready, _ => { }, RenderBody, items);
		Assert.Equal("class,C\n\tmember,M\n\t\tlocalFunction,F\n\tmember,N\n", Assert.IsType<PagedRender.Complete>(whole).Output);

		PagedRender continuation = ItemPaging.Render(
			SolutionStatus.Ready, _ => { }, RenderBody, items, startItem: 2, maxSize: 100);
		Assert.Equal("class,C\n\tmember,N\n", RawBody(Assert.IsType<PagedRender.Page>(continuation).Output));
	}

	[Fact]
	public void WhenCountOnlyHasNoItems_ThenTheResultIsCountZeroAndStartItemIsIgnored()
	{
		PagedRender render = ItemPaging.Render(
			SolutionStatus.Ready,
			builder => builder.Header("resolvedType", "Ns.C"),
			_ => { },
			[],
			startItem: 7,
			maxSize: 1,
			countOnly: true);

		string output = Assert.IsType<PagedRender.Complete>(render).Output;

		// startItem 7 is past an empty set and maxSize is 1: both are page parameters, ignored by countOnly.
		Assert.Equal("resolvedType=Ns.C\ncount=0\n", output);
		Assert.DoesNotContain("\n\n", output);
	}

	[Fact]
	public void WhenCountOnlyCountsMixedKinds_ThenPerKindLinesFollowTheVocabularyOrder()
	{
		var items = new[]
		{
			OutlineItem.Single("property,Count,3:5", []),
			new OutlineItem([(0, "method,Combine"), (1, "localfunction,Helper")], []),
			OutlineItem.Single("field,_ready,4:5", []),
			OutlineItem.Single("CS0246,9:3,A message", []),
		};

		PagedRender render = ItemPaging.Render(SolutionStatus.Ready, _ => { }, _ => { }, items, countOnly: true);

		string output = Assert.IsType<PagedRender.Complete>(render).Output;

		// count= is the item total (4), while the per-kind lines count leaves: the member item carries a
		// nested local function, so both of its leaves are counted, and the diagnostic line - whose first
		// field is an id, not a kind - is not counted by kind.
		Assert.Equal(
			"count=4\ncount.method=1\ncount.property=1\ncount.field=1\ncount.localfunction=1\n",
			output);
	}

	[Fact]
	public void WhenCountOnlyIsSet_ThenNoPagingHeadersOrBlankLineAppear()
	{
		var items = Enumerable.Range(1, 5)
			.Select(index => OutlineItem.Single($"method,M{index}", []))
			.ToArray();

		PagedRender render = ItemPaging.Render(
			SolutionStatus.Building, _ => { }, _ => { }, items, startItem: 2, maxSize: 1, countOnly: true);

		string output = Assert.IsType<PagedRender.Complete>(render).Output;

		Assert.Equal("status=Building\ncount=5\ncount.method=5\n", output);
		Assert.DoesNotContain("truncated=", output);
		Assert.DoesNotContain("nextStartItem=", output);
		Assert.DoesNotContain("partialItem=", output);
		Assert.DoesNotContain("\n\n", output);
	}

	[Fact]
	public void WhenCountOnlyHasACapHeader_ThenTheCapAddsNoSecondCount()
	{
		var items = new[] { OutlineItem.Single("method,A", []), OutlineItem.Single("method,B", []) };

		PagedRender render = ItemPaging.Render(
			SolutionStatus.Ready,
			_ => { },
			_ => { },
			items,
			capHeaders: builder => builder.Header("truncated", true),
			countOnly: true);

		string output = Assert.IsType<PagedRender.Complete>(render).Output;

		Assert.Equal("truncated=Y\ncount=2\ncount.method=2\n", output);
		Assert.Single(output.Split('\n'), line => line.StartsWith("count=", StringComparison.Ordinal));
	}

	/// <summary>The body of a rendered page: everything after the headers' blank line.</summary>
	private static string RawBody(string page)
	{
		int separator = page.IndexOf("\n\n", StringComparison.Ordinal);
		return separator < 0 ? "" : page[(separator + 2)..];
	}

	private static string Header(string page, string key)
	{
		foreach (string line in page.Split('\n'))
		{
			if (line.StartsWith(key + "=", StringComparison.Ordinal))
				return line[(key.Length + 1)..];
		}

		return "";
	}

	private static int CountOccurrences(string text, string token)
	{
		int count = 0;
		int at = 0;
		while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
		{
			count++;
			at += token.Length;
		}

		return count;
	}
}
