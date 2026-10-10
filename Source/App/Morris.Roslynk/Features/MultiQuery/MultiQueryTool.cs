using System.ComponentModel;
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
	/// The op-count limit (Decision 1). Operations beyond it are not executed; each still gets a numbered
	/// slot carrying a whole error=Truncated block, so the caller can re-send exactly the missing ops.
	/// </summary>
	public const int MaxOperations = 25;

	/// <summary>
	/// Below this a slot cannot carry even its headers usefully, so it is refused rather than run: its
	/// allowance is smaller than one whole error block would be worth spending a result on.
	/// </summary>
	private const int MinSlotChars = 2_000;

	/// <summary>An upper bound on the bracketed trailer a cut slot appends (see <see cref="CutTrailer"/>).</summary>
	private const int BudgetTrailerBound = 512;

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

	[McpServerTool(
		Name = MultiQueryName,
		Title = "Run several read-only queries in one call",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Executes several read-only operations in one call, against a single consistent snapshot of the
		solution, and returns one result per operation in request order. Operations are independent - no
		operation may reference another's output.
		When you need several facts before making a change, send them as one multi_query call rather than as
		sequential calls.
		For usage/impact questions, batch find_references, get_callers, get_callees,
		find_implementations and get_type_hierarchy in one call.
		Read-only query tools only: get_symbol, get_symbol_body, get_members, find_definition,
		find_implementations, find_references, get_callers, get_callees, search_symbols, get_type_hierarchy, find_dead_code,
		find_dead_conditionals, get_expression_info. Write tools, get_diagnostics and get_solution_status are not multi-queryable;
		get_diagnostics and get_solution_status are ordinary single calls.
		Each operation takes exactly that tool's single-call parameter names; unknown or misspelled parameters
		are rejected rather than ignored, and omitted parameters take the tool's declared defaults.
		The batch carries at most 25 operations, and the whole response stays within the server's response
		budget (about 80,000 characters by default; set Roslynk:MaxResponseChars to change it - the envelope
		echoes it as budget=<n>). Each operation gets what earlier slots left: get_symbol_body pages itself
		within its share (truncated=Y/nextStartLine= in its slot); any other tool's output that would not fit
		is cut after its last whole line - its meta line gains truncated=Y and its body ends with a bracketed
		[response budget exhausted: ...] note naming the continuation. Trust rule: a slot whose META LINE
		carries truncated=Y was cut by the batch budget, so its body is a prefix - do not derive tool-level
		paging (e.g. nextStartLine) from it; re-send that operation alone. Only a complete slot's own body
		headers may drive tool-level paging. Remaining slots are whole error=Truncated blocks
		(truncatedSlots=<n> counts every slot that did not deliver a complete result) - re-send exactly those
		operations, from your own copy of the request (slot meta lines carry the index and tool name only,
		never the arguments). The header names the snapshot=<id> every slot was computed against; pass it back
		as expectSnapshot when you continue so a continuation that would straddle two generations of the
		solution (a write, or the user saving a file) is refused with error=Stale instead of silently mixing
		them - on Stale, re-run the whole batch.
		{OutlineDescriptions.Freshness}
		""")]
	public async Task<string> MultiQuery(
		[Description("Solution handle returned by open_solution; every operation runs against this one solution's snapshot.")] string solutionId,
		[Description("The operations to run, in order. Each names a read-only tool and carries that tool's arguments.")] IReadOnlyList<MultiQueryOperation> operations,
		[Description("Optional: the snapshot=<id> from a previous multi_query response, checked before anything runs; a mismatch is error=Stale naming both ids - re-run the whole batch. Omit for a one-shot batch.")] string? expectSnapshot = null)
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

		// Continuation seam check (Decision 8): refuse before any op runs, so a rejected continuation costs
		// nothing. The current id is emitted as a real header (the candidate= precedent) so the caller can
		// see exactly which generation it would have been stitching onto.
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
	/// The budget is a hard ceiling on the whole rendered envelope: everything that is not slot output is
	/// reserved up front (the header, one frame per slot at its widest, the widest truncatedSlots footer and
	/// the terminator), each slot is handed exactly what remains (less what later slots need at their
	/// minimum, one refused block each), and a non-fitting body is cut after a whole line or refused whole.
	/// A cut only removes a tail and appends a bracketed trailer, so framing integrity is untouched: the
	/// boundary is a fresh random GUID absent from any verbatim body, and the n+1 check below still faults
	/// loudly on any envelope bug.
	/// </remarks>
	internal async Task<string> ExecuteBatchAsync(
		SolutionModel pinnedModel,
		RoslynInstance instance,
		IReadOnlyDictionary<string, MultiQueryCatalog.OpEntry> catalog,
		IReadOnlyList<MultiQueryOperation> operations,
		string? boundary = null)
	{
		string effectiveBoundary = boundary ?? Guid.NewGuid().ToString("N");
		var envelope = new StringBuilder();
		envelope.Append("operations=").Append(operations.Count).Append('\n');
		envelope.Append("snapshot=").Append(pinnedModel.Id.ToString("N")).Append('\n');
		envelope.Append("boundary=").Append(effectiveBoundary).Append('\n');
		envelope.Append("budget=").Append(Budget.MaxChars).Append('\n');

		// Blocks for refused slots are deterministic: format each once and account its real length.
		string overCount = OutlineError.Format(
			Error.Truncated($"At most {MaxOperations} operations per batch. This operation was not run; re-send it (with its original arguments) to continue."),
			pinnedModel.Status);
		string overBudget = OutlineError.Format(
			Error.Truncated("Response budget exhausted. This operation was not run; re-send it (with its original arguments) in a new multi_query, or call the tool directly."),
			pinnedModel.Status);

		// Everything the envelope writes whatever the slots return: the header so far, one frame per slot
		// (at its widest, with the truncated=Y meta marker), the widest truncatedSlots footer, the terminator.
		int fixedChars = envelope.Length + Terminator(effectiveBoundary).Length;
		for (int index = 0; index < operations.Count; index++)
		{
			fixedChars += Math.Max(
				SlotFrame(effectiveBoundary, index, operations[index].Tool, cut: false).Length,
				SlotFrame(effectiveBoundary, index, operations[index].Tool, cut: true).Length);
		}
		fixedChars += $"truncatedSlots={operations.Count}\n".Length;

		// later[i] is the least the slots from i onward can cost: each is at minimum one refused block,
		// whose actual measured length is what sums here.
		var later = new int[operations.Count + 1];
		for (int index = operations.Count - 1; index >= 0; index--)
			later[index] = later[index + 1] + (index >= MaxOperations ? overCount.Length : overBudget.Length);

		if (fixedChars + later[0] > Budget.MaxChars)
			return OutlineError.Format(
				Error.Invalid(
					$"{operations.Count} operations cannot be framed within the {Budget.MaxChars}-character response budget. Send at most {MaxOperations}."),
				pinnedModel.Status);

		int truncatedCount = 0;   // every slot that did not deliver a complete result: refused OR cut
		int bodyChars = 0;
		for (int index = 0; index < operations.Count; index++)
		{
			// The two truncation triggers (Decisions 1 and 5): an op beyond the count limit, or a slot whose
			// allowance cannot carry a useful result, is not executed - its slot carries a whole
			// error=Truncated block naming why, so the caller can re-send exactly these operations.
			int allowance = Budget.MaxChars - fixedChars - bodyChars - later[index + 1];
			string body;
			bool cut = false;
			if (index >= MaxOperations)
			{
				truncatedCount++;
				body = overCount;
				bodyChars += overCount.Length;
			}
			else if (allowance < MinSlotChars)
			{
				truncatedCount++;
				body = overBudget;
				bodyChars += overBudget.Length;
			}
			else
			{
				// A budget-aware core pages itself within the allowance (get_symbol_body reports
				// nextStartLine); anything else is cut after its last whole line and marked - or refused
				// whole when not even one line fits, because a torn verbatim body is worse than a refusal.
				body = await ExecuteOperationAsync(pinnedModel, instance, catalog, operations[index], ResponseBudget.Allowance(allowance)).ConfigureAwait(false);
				if (body.Length > allowance)
				{
					truncatedCount++;
					string prefix = LongestWholeLinePrefix(body, allowance - BudgetTrailerBound);
					if (prefix.Length == 0)
					{
						body = overBudget;
						bodyChars += overBudget.Length;
					}
					else
					{
						cut = true;
						body = prefix + CutTrailer(
							index + 1,
							operations[index].Tool.ToString(),
							LineWindow.CountLines(prefix),
							LineWindow.CountLines(body),
							pinnedModel.Id.ToString("N"));
						bodyChars = Budget.MaxChars - fixedChars - later[index + 1]; // a cut spends the rest
					}
				}
				else
				{
					bodyChars += body.Length;
				}
			}

			// The frame's cut variant (with truncated=Y) is what fixedChars reserved; a non-cut slot renders
			// narrower, so the rendered envelope never exceeds the reserved ceiling.
			envelope.Append(SlotFrame(effectiveBoundary, index, operations[index].Tool, cut));
			envelope.Append(body);
		}

		if (truncatedCount > 0)
			envelope.Append("truncatedSlots=").Append(truncatedCount).Append('\n');
		envelope.Append(Terminator(effectiveBoundary));

		string rendered = envelope.ToString();

		// Framing integrity (Decision 2, belt-and-braces): the delimiter-line prefix must occur exactly
		// n+1 times - n slot delimiters plus the terminator, which starts with the same prefix. Counting the
		// bare hex would also hit the boundary= header (n+2); counting the prefix does not. A mismatch is an
		// envelope bug and must fault loudly rather than ship a silently desynced response.
		string delimiterToken = "\n--" + effectiveBoundary;
		int occurrences = 0;
		int at = 0;
		while ((at = rendered.IndexOf(delimiterToken, at, StringComparison.Ordinal)) >= 0)
		{
			occurrences++;
			at += delimiterToken.Length;
		}
		if (occurrences != operations.Count + 1)
			throw new InvalidOperationException(
				$"multi_query envelope integrity check failed: expected {operations.Count + 1} delimiter lines, found {occurrences}.");

		return rendered;
	}

	/// <summary>
	/// The longest prefix of <paramref name="body"/> that ends exactly after a '\n' and is at most
	/// <paramref name="limit"/> characters - empty when not even one line break fits, which refuses the slot
	/// rather than tearing the body.
	/// </summary>
	private static string LongestWholeLinePrefix(string body, int limit)
	{
		if (limit < 1)
			return "";

		int searchFrom = Math.Min(limit, body.Length) - 1;
		int lastBreak = searchFrom >= 0 ? body.LastIndexOf('\n', searchFrom) : -1;
		return lastBreak < 0 ? "" : body[..(lastBreak + 1)];
	}

	/// <summary>
	/// The bracketed continuation note a cut slot's body ends with. Bracketed so it cannot read as tool
	/// output; the machine signal is the slot meta line's truncated=Y. Bound by BudgetTrailerBound.
	/// </summary>
	private static string CutTrailer(int slot, string tool, int shownLines, int totalLines, string snapshotId) =>
		$"[response budget exhausted: slot {slot} ({tool}) was cut at a line boundary; {shownLines} of {totalLines} lines of its output shown. Re-send this operation (with its original arguments) in a new multi_query, or call the tool directly, to get the rest; pass expectSnapshot={snapshotId} to pin the same solution generation.]";

	private static string SlotFrame(string boundary, int index, MultiQueryOp tool, bool cut) =>
		$"\n--{boundary}\nslot={index + 1} tool={tool}{(cut ? " truncated=Y" : "")}\n\n";

	private static string Terminator(string boundary) => $"\n--{boundary}--\n";

	private async Task<string> ExecuteOperationAsync(
		SolutionModel pinnedModel,
		RoslynInstance pinnedInstance,
		IReadOnlyDictionary<string, MultiQueryCatalog.OpEntry> catalog,
		MultiQueryOperation operation,
		ResponseBudget? allowance)
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
			return await MultiQueryCatalog.InvokeCoreAsync(
				Provider,
				entry,
				pinnedModel,
				pinnedInstance,
				allowance,
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
