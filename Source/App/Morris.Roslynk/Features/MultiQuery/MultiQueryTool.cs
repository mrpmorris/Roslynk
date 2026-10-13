using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk.Features.MultiQuery;

[McpServerToolType]
public sealed class MultiQueryTool
{
	public const string MultiQueryName = "multi_query";

	/// <summary>
	/// The 'retry=' line reserved in the budget even when it turns out empty, so the model's planning
	/// arithmetic is exact: a continuation batch whose dropped operations sum to ratios of at most 1.0
	/// fits the same envelope shape as this one.
	/// </summary>
	private const int RetryReserveChars = 7; // "retry=\n"

	private static readonly IReadOnlyDictionary<string, JsonElement> NoArguments =
		new Dictionary<string, JsonElement>(StringComparer.Ordinal);

	private readonly IServiceProvider Provider;
	private readonly InstanceRegistry InstanceRegistry;
	private readonly ResponseBudget Budget;

	public MultiQueryTool(IServiceProvider provider, InstanceRegistry instanceRegistry, ResponseBudget? responseBudget = null)
	{
		Provider = provider ?? throw new ArgumentNullException(nameof(provider));
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		Runs independent read-only operations against one snapshot, returning results in request order.
		Operations cannot reference each other's output. Batch only queries needed for the question.

		operations.tool uses the schema enum; arguments use that tool's single-call names/defaults.
		Unknown/misspelled arguments are rejected.

		All operations run. budget=<n> is the per-operation allowance; whole results are selected
		smallest-first until the response budget fills. Slots are complete or absent. Missing results
		report retry=<index>:<size> in request order; size is result cost/budget, rounded up to 0.1.
		- size >= 1.0: resend alone.
		- Sizes totaling <= 1.0: fit one continuation batch.
		- Sizes below 0.1: about ten per continuation batch is safe.

		Resend retry operations with original arguments from your request copy; frames contain only
		index/tool. Pass snapshot=<id> as expectSnapshot; changed generations return Stale with both ids:
		rerun the whole batch. Ratios are snapshot-specific; reruns remeasure/reselect.
		Only slot-body nextStartItem drives paging; retry means an omitted result, not a page.
		""")]
	[McpServerTool(
		Name = MultiQueryName,
		Title = "Run several read-only queries in one call",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
		public async Task<string> MultiQuery(
			[Description("Solution handle.")] string solutionId,
			[Description("Read-only tools and arguments, in request order.")] IReadOnlyList<MultiQueryOperation> operations,
			[Description("Previous snapshot id; mismatch returns Stale before execution.")] string? expectSnapshot = null)
	{
		// Structural validation first: an uninterpretable batch cannot produce slots. (An op naming a tool
		// outside the enum never reaches here - the SDK's binding layer refuses it and RoslynkTool renders
		// the standard header-only Invalid.)
		if (operations is null || operations.Count == 0)
			return OutlineError.Format(Error.Invalid("At least one operation is required."), SolutionStatus.Ready);

		// One snapshot for the whole batch: acquire once, pin once. Every core runs on this model, so no two
		// slots can observe different generations of the solution.
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		// While loading there is no snapshot to run anything against - the whole batch is Indexing, exactly
		// like every other tool, rather than n slots each reporting the same condition.
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		// Continuation seam check: refuse before any op runs, so a rejected continuation costs nothing. The
		// current id is emitted as a real header (the candidate= precedent) so the caller can see exactly
		// which generation it would have been stitching onto.
		if (expectSnapshot is not null)
		{
			if (!Guid.TryParse(expectSnapshot, out Guid expected))
				return OutlineError.Format(
					Error.Invalid($"'expectSnapshot' must be a snapshot=<id> from a previous multi_query response, got '{expectSnapshot}'."),
					model.Status);

			if (expected != model.Id)
			{
				return OutlineError.Format(
					Error.Stale($"The solution moved on since the snapshot you expected (expected {expected.ToString("N")}, current {model.Id.ToString("N")}). Re-run the whole batch rather than stitching two generations."),
					model.Status)
					+ $"snapshot={model.Id.ToString("N")}\n";
			}
		}

		return await ExecuteBatchAsync(model, instance, MultiQueryCatalog.Entries, operations).ConfigureAwait(false);
	}

	/// <summary>
	/// The seam the public method delegates to: a pinned model plus the catalog to resolve operations
	/// against. Internal so tests can pass a deliberately stale model or a stub catalog - the two things the
	/// public surface must never accept.
	/// </summary>
	/// <remarks>
	/// Every operation runs (speculatively - an op whose result is dropped has still cost its query), each
	/// result is measured as its frame plus body, and whole results are selected smallest-first until the
	/// budget is filled. Selection maximises the number of queries answered; the count-optimal order is
	/// smallest-first (an exchange argument: any selection can be improved by swapping a chosen result for
	/// a smaller unchosen one), with exact-size ties broken by request index. Delivered slots are emitted
	/// in request order; dropped operations become one retry= header line. The batch limit is not a
	/// constant but the framing itself: a batch whose worst-case retry line cannot fit the budget is
	/// refused before anything runs.
	/// </remarks>
	internal async Task<string> ExecuteBatchAsync(
		SolutionModel pinnedModel,
		RoslynInstance pinnedInstance,
		IReadOnlyDictionary<string, MultiQueryCatalog.OpEntry> catalog,
		IReadOnlyList<MultiQueryOperation> operations,
		string? boundary = null)
	{
		string effectiveBoundary = boundary ?? Guid.NewGuid().ToString("N");

		// The net budget echoed to the caller: the raw ceiling less this envelope's own fixed framing (the
		// base header, the terminator and the retry= prefix a continuation may need). Iterated because the
		// echoed value's own width is part of what it is subtracted from; converges immediately.
		int terminatorChars = Terminator(effectiveBoundary).Length;
		int net = Budget.MaxChars;
		for (int iteration = 0; iteration < 3; iteration++)
		{
			int headerChars = BaseHeader(operations.Count, pinnedModel.Id, effectiveBoundary, net).Length;
			net = Math.Max(1, Budget.MaxChars - headerChars - terminatorChars - RetryReserveChars);
		}

		string header = BaseHeader(operations.Count, pinnedModel.Id, effectiveBoundary, net);

		// Derived batch limit: the retry line must fit even when every operation is dropped. Each entry is
		// then at its narrowest ('<index>:0.1'), so a batch whose minimum-width retry line cannot fit is
		// refused before anything executes - no arbitrary operation count.
		int minimumRetryChars = RetryLine(Enumerable.Range(0, operations.Count).Select(MinimumRetryEntry).ToArray()).Length;
		if (header.Length + minimumRetryChars + terminatorChars > Budget.MaxChars)
		{
			return OutlineError.Format(
				Error.Invalid(
					$"{operations.Count} operations cannot be framed within the {Budget.MaxChars}-character response budget: the retry line alone would not fit. Send fewer operations."),
				pinnedModel.Status);
		}

		// Every operation runs, against the one pinned snapshot. Results land by index; an operation whose
		// core fails carries its own error block, so one bad query never aborts the batch.
		var bodies = new string[operations.Count];
		await Parallel.ForEachAsync(
			Enumerable.Range(0, operations.Count),
			async (index, _) =>
				bodies[index] = await ExecuteOperationAsync(pinnedModel, pinnedInstance, catalog, operations[index]).ConfigureAwait(false))
			.ConfigureAwait(false);

		// Costs: frame + body for a delivered slot, one small header entry for a dropped one.
		int[] cost = new int[operations.Count];
		for (int index = 0; index < operations.Count; index++)
			cost[index] = SlotFrame(effectiveBoundary, index, operations[index].Tool).Length + bodies[index].Length;

		// Smallest-first selection. total(k) is exact - including the retry line the complement needs - and
		// strictly increasing in k (delivering an operation costs far more than its retry entry), so the
		// first k that exceeds the budget bounds every larger selection too.
		int[] order = Enumerable.Range(0, operations.Count).OrderBy(index => cost[index]).ThenBy(index => index).ToArray();
		string RetryFor(IEnumerable<int> dropped) => RetryLine(dropped.OrderBy(index => index).Select(index => RetryEntry(index, cost[index], net)).ToArray());
		int Total(int taken) =>
			header.Length + terminatorChars
			+ (taken == operations.Count ? 0 : RetryFor(order[taken..]).Length)
			+ order.Take(taken).Sum(index => cost[index]);

		if (Total(0) > Budget.MaxChars)
		{
			return OutlineError.Format(
				Error.Invalid(
					$"{operations.Count} operations cannot be framed within the {Budget.MaxChars}-character response budget: the retry line alone would not fit. Send fewer operations."),
				pinnedModel.Status);
		}

		int delivered = 0;
		while (delivered < operations.Count && Total(delivered + 1) <= Budget.MaxChars)
			delivered++;

		var envelope = new StringBuilder();
		envelope.Append(header);
		if (delivered < operations.Count)
			envelope.Append(RetryFor(order[delivered..]));
		foreach (int index in order[..delivered].OrderBy(index => index))
		{
			envelope.Append(SlotFrame(effectiveBoundary, index, operations[index].Tool));
			envelope.Append(bodies[index]);
		}
		envelope.Append(Terminator(effectiveBoundary));

		string rendered = envelope.ToString();

		// Framing integrity, belt-and-braces: the delimiter-line prefix must occur exactly once per
		// delivered slot plus the terminator, which starts with the same prefix. A mismatch is an envelope
		// bug and must fault loudly rather than ship a silently desynced response.
		string delimiterToken = "\n--" + effectiveBoundary;
		int occurrences = 0;
		int at = 0;
		while ((at = rendered.IndexOf(delimiterToken, at, StringComparison.Ordinal)) >= 0)
		{
			occurrences++;
			at += delimiterToken.Length;
		}
		if (occurrences != delivered + 1)
			throw new InvalidOperationException(
				$"multi_query envelope integrity check failed: expected {delivered + 1} delimiter lines, found {occurrences}.");

		return rendered;
	}

	/// <summary>The four header lines every envelope carries; budget= echoes the net per-operation allowance.</summary>
	private static string BaseHeader(int operationCount, Guid snapshotId, string boundary, int netBudget) =>
		$"operations={operationCount}\n" +
		$"snapshot={snapshotId.ToString("N")}\n" +
		$"boundary={boundary}\n" +
		$"budget={netBudget}\n";

	/// <summary>A dropped operation's retry entry: request index and result cost as a ratio of the net
	/// budget, rounded up to 0.1 - 0.1 is the floor, so nothing ever reads as free.</summary>
	private static string RetryEntry(int index, int cost, int netBudget)
	{
		double ratio = Math.Ceiling(cost * 10d / netBudget) / 10d;
		if (ratio < 0.1)
			ratio = 0.1;
		return $"{index + 1}:{ratio.ToString("0.0", CultureInfo.InvariantCulture)}";
	}

	/// <summary>The narrowest entry an index can produce ('<index>:0.1'), for the pre-flight batch limit.</summary>
	private static string MinimumRetryEntry(int index) => $"{index + 1}:0.1";

	private static string RetryLine(string[] entries) =>
		entries.Length == 0 ? "" : $"retry={string.Join(",", entries)}\n";

	private static string SlotFrame(string boundary, int index, MultiQueryOp tool) =>
		$"\n--{boundary}\nslot={index + 1} tool={tool}\n\n";

	private static string Terminator(string boundary) => $"\n--{boundary}--\n";

	private async Task<string> ExecuteOperationAsync(
		SolutionModel pinnedModel,
		RoslynInstance pinnedInstance,
		IReadOnlyDictionary<string, MultiQueryCatalog.OpEntry> catalog,
		MultiQueryOperation operation)
	{
		if (!catalog.TryGetValue(operation.Tool.ToString(), out MultiQueryCatalog.OpEntry? entry))
		{
			// Unreachable over the wire: the enum schema makes any other name unbindable. This branch serves
			// the internal seam (tests) and keeps the failure shape defined should the enum ever widen.
			return OutlineError.Format(
				Error.NotFound($"No multi-queryable tool named '{operation.Tool}'.", MultiQueryCatalog.Entries.Keys.ToList()),
				pinnedModel.Status);
		}

		try
		{
			// No allowance: a batch slot is whole or absent, so the core renders its complete result and the
			// batch either delivers it or names it in retry=. Explicit paging arguments (startItem,
			// maxSize) still page - a continuation re-sending one oversized operation relies on that.
			return await MultiQueryCatalog.InvokeCoreAsync(
				Provider,
				entry,
				pinnedModel,
				pinnedInstance,
				allowance: null,
				operation.Arguments ?? NoArguments,
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// Isolation boundary: one operation's failure becomes its slot's error block and the batch
			// continues. Formatted with the pinned model's status so a slot error during Building still
			// carries its status header.
			return OutlineError.Format(RoslynkTool.ToError(exception), pinnedModel.Status);
		}
	}
}
