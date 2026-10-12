namespace Morris.Roslynk.Infrastructure.Outlines;

using Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// One item of a paged result: the line(s) that carry the data, plus the container lines open above them.
/// <see cref="Lines"/> are relative to the chain — the first line renders at depth <c>Context.Length</c>,
/// each further line at <c>Context.Length + Depth</c> (get_members nests a member's local functions under
/// it). A page re-emits each item's chain suffix, so every page is self-contained: a subsequence of the
/// very lines the whole render writes, never fabricated text.
/// </summary>
public sealed record OutlineItem((int Depth, string Text)[] Lines, string[] Context)
{
	public static OutlineItem Single(string line, string[] context) => new([(0, line)], context);
}

/// <summary>
/// The outcome of <see cref="ItemPaging.Render"/>: the whole result (byte-identical to the unpaged
/// output), one page of it, or a rejection naming what the caller must change.
/// </summary>
public abstract record PagedRender
{
	/// <summary>The complete result — no paging headers, the body the tool has always written.</summary>
	public sealed record Complete(string Output) : PagedRender;

	/// <summary>One page: the tool's headers plus count=/truncated=Y and, when items remain, nextStartItem=.</summary>
	public sealed record Page(string Output) : PagedRender;

	/// <summary>The request cannot be served by any page: startItem past the end, or maxSize too small.</summary>
	public sealed record Invalid(string Message) : PagedRender;
}

/// <summary>
/// Item-based paging shared by every query tool whose result can be too large. An item is one leaf
/// record (a reference, caller, diagnostic, member, search hit — for get_symbol_body, one source line);
/// a page carries as many whole items as fit within the character ceiling, re-emitting the container
/// chain so the page reads standalone. The first page of a fitting result is the unpaged output itself;
/// paging headers exist only when items were left out.
/// </summary>
/// <remarks>
/// Packing is greedy front-to-back: items keep their render order, so pages are contiguous ranges and a
/// continuation is just a larger <c>startItem</c>. A chain suffix is measured against the chain still
/// open from the previously packed item — when an item's own line is the container its successor hangs
/// under (a matched type with matched members), the suffix is empty and the line is not repeated.
/// </remarks>
internal static class ItemPaging
{
	/// <summary>The default <paramref name="maxSize"/>: page at the response budget when a budget is
	/// supplied (stand-alone calls), and render whole when none is (multi_query measures whole results).</summary>
	public const int AutoPageSize = 0;

	public static PagedRender Render(
		SolutionStatus status,
		Action<OutlineBuilder> addHeaders,
		Action<OutlineBuilder> renderBody,
		IReadOnlyList<OutlineItem> items,
		int startItem = 1,
		int maxSize = AutoPageSize,
		ResponseBudget? budget = null,
		Action<OutlineBuilder>? capHeaders = null)
	{
		if (startItem < 1)
			return new PagedRender.Invalid($"'startItem' counts items from 1 (got {startItem}).");
		if (maxSize < 0)
			return new PagedRender.Invalid($"'maxSize' must be at least 0 (got {maxSize}).");

		int ceiling = (budget ?? ResponseBudget.Default).MaxChars;
		bool explicitPaging = startItem > 1 || maxSize > 0;
		if (!explicitPaging && budget is null)
			return new PagedRender.Complete(Compose(status, addHeaders, capHeaders, renderBody));

		int pageChars = maxSize > 0 ? Math.Min(maxSize, ceiling) : ceiling;
		string whole = Compose(status, addHeaders, capHeaders, renderBody);
		if (startItem == 1 && (whole.Length <= pageChars || items.Count == 0))
			return new PagedRender.Complete(whole);

		if (startItem > items.Count)
			return new PagedRender.Invalid($"'startItem' is past the end: the result has {items.Count} items.");

		var builder = new OutlineBuilder();
		addHeaders(builder);
		capHeaders?.Invoke(builder);
		builder.Status(status);

		// Reserve the widest paging header block up front, so packing cannot spend characters the real
		// headers then take back; +1 is the blank line between headers and body.
		int headerChars = builder.ToString().Length + 1
			+ "truncated=Y\n".Length
			+ $"count={items.Count}\n".Length
			+ $"nextStartItem={items.Count + 1}\n".Length
			+ $"partialItem={items.Count}\n".Length;
		int bodyBudget = Math.Max(1, pageChars - headerChars);

		var lines = new List<(int Depth, string Text)>();
		string[] open = [];
		int index = startItem - 1;
		int partial = 0;
		int used = 0;

		while (index < items.Count)
		{
			OutlineItem item = items[index];
			int common = CommonPrefix(open, item.Context);
			int suffixCost = 0;
			for (int depth = common; depth < item.Context.Length; depth++)
				suffixCost += LineCost(depth, item.Context[depth]);
			int itemCost = item.Lines.Sum(line => LineCost(item.Context.Length + line.Depth, line.Text));

			if (suffixCost + itemCost <= bodyBudget - used)
			{
				for (int depth = common; depth < item.Context.Length; depth++)
					lines.Add((depth, item.Context[depth]));
				foreach ((int depth, string text) in item.Lines)
					lines.Add((item.Context.Length + depth, text));

				used += suffixCost + itemCost;
				open = OpenAfter(item, index + 1 < items.Count ? items[index + 1].Context : null);
				index++;
				continue;
			}

			if (index == startItem - 1)
			{
				// The page's first item does not fit. An item larger than the whole body budget can never
				// be delivered whole by any page: deliver its container chain plus as much of it as fits
				// and name it. An item that would fit a fresh page but not beside its chain means maxSize
				// is simply too small.
				if (itemCost <= bodyBudget)
				{
					return new PagedRender.Invalid(
						$"'maxSize' is too small: item {index + 1} needs {suffixCost + itemCost} characters including its context lines. "
						+ $"The item itself is {itemCost} characters; raise maxSize to at least {headerChars + suffixCost + itemCost}.");
				}

				if (suffixCost >= bodyBudget)
				{
					return new PagedRender.Invalid(
						$"'maxSize' is too small: item {index + 1}'s context alone is {suffixCost} characters.");
				}

				int remaining = bodyBudget - suffixCost;
				for (int depth = common; depth < item.Context.Length; depth++)
					lines.Add((depth, item.Context[depth]));
				bool emittedAny = false;
				foreach ((int depth, string text) in item.Lines)
				{
					int cost = LineCost(item.Context.Length + depth, text);
					if (cost <= remaining)
					{
						lines.Add((item.Context.Length + depth, text));
						remaining -= cost;
						emittedAny = true;
						continue;
					}

					// A single-line item longer than the whole body budget: the chain plus the line's
					// prefix is all any page can carry - the rest of the line is not retrievable.
					if (!emittedAny && item.Lines.Length == 1 && remaining - item.Context.Length - 1 >= 1)
					{
						int take = ResponseBudget.SafeCut(text, 0, remaining - item.Context.Length - 1);
						lines.Add((item.Context.Length + depth, text[..take]));
					}

					break;
				}

				if (!emittedAny && item.Lines.Length > 1)
				{
					return new PagedRender.Invalid(
						$"'maxSize' is too small: item {index + 1} needs {suffixCost + itemCost} characters including its context lines.");
				}

				partial = index + 1;
				index++;
				break;
			}

			break;
		}

		bool more = index < items.Count;
		if (partial > 0)
			builder.Header("partialItem", partial);
		if (capHeaders is not null)
		{
			// The caller already wrote count=/truncated=Y for its capped set; only the continuation
			// pointer is ours to add.
		}
		else if (more || partial > 0)
		{
			builder.Header("truncated", true);
			builder.Header("count", items.Count);
		}
		else
		{
			// A continuation page that completes the result: count= names the set, but nothing was
			// dropped, so there is no truncated= and nothing to continue.
			builder.Header("count", items.Count);
		}
		if (more)
			builder.Header("nextStartItem", index + 1);
		builder.BeginBody();
		foreach ((int depth, string text) in lines)
			builder.Line(depth, text);
		return new PagedRender.Page(builder.ToString());
	}

	private static string Compose(SolutionStatus status, Action<OutlineBuilder> addHeaders, Action<OutlineBuilder>? capHeaders, Action<OutlineBuilder> renderBody)
	{
		var builder = new OutlineBuilder();
		addHeaders(builder);
		capHeaders?.Invoke(builder);
		builder.Status(status);
		builder.BeginBody();
		renderBody(builder);
		return builder.ToString();
	}

	private static int LineCost(int depth, string text) => depth + text.Length + 1;

	/// <summary>
	/// The chain still open after <paramref name="item"/> is packed: its own context, extended by its first
	/// line when the next item hangs beneath that line — the matched-type-with-matched-members case, where
	/// the render writes the line once and both the item and its children sit under it.
	/// </summary>
	private static string[] OpenAfter(OutlineItem item, string[]? nextContext)
	{
		if (nextContext is null || item.Lines[0].Depth != 0)
			return item.Context;

		string[] extended = new string[item.Context.Length + 1];
		item.Context.CopyTo(extended, 0);
		extended[^1] = item.Lines[0].Text;
		return PrefixMatches(nextContext, extended) ? extended : item.Context;
	}

	private static bool PrefixMatches(string[] candidate, string[] prefix)
	{
		if (candidate.Length < prefix.Length)
			return false;
		for (int index = 0; index < prefix.Length; index++)
		{
			if (!string.Equals(candidate[index], prefix[index], StringComparison.Ordinal))
				return false;
		}

		return true;
	}

	private static int CommonPrefix(string[] open, string[] context)
	{
		int shared = Math.Min(open.Length, context.Length);
		for (int index = 0; index < shared; index++)
		{
			if (!string.Equals(open[index], context[index], StringComparison.Ordinal))
				return index;
		}

		return shared;
	}
}
