using System.ComponentModel;
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
	/// The response budget (Decision 5). A slot is never cut mid-body: once the accumulated output crosses
	/// the line, remaining slots are whole error=Truncated blocks - so the cap bounds overshoot to one op's
	/// output rather than enforcing a hard ceiling, and a caller can always tell a complete answer from a
	/// missing one.
	/// </summary>
	private const int MaxTotalOutputChars = 200_000;

	private static readonly IReadOnlyDictionary<string, JsonElement> NoArguments =
		new Dictionary<string, JsonElement>(StringComparer.Ordinal);

	private readonly IServiceProvider Provider;
	private readonly InstanceRegistry InstanceRegistry;

	public MultiQueryTool(IServiceProvider provider, InstanceRegistry instanceRegistry)
	{
		Provider = provider ?? throw new ArgumentNullException(nameof(provider));
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
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
		Read-only query tools only: get_symbol, get_symbol_body, get_members, find_definition,
		find_implementations, find_references, get_callers, get_callees, search_symbols, get_type_hierarchy,
		find_dead_code, find_dead_conditionals. Write tools, get_diagnostics and get_solution_status are not
		multi-queryable; get_diagnostics and get_solution_status are ordinary single calls.
		Each operation takes exactly that tool's single-call parameter names; unknown or misspelled parameters
		are rejected rather than ignored, and omitted parameters take the tool's declared defaults.
		The batch carries at most 25 operations: more are not run and their slots return
		error=Truncated (truncatedSlots=<n> in the header) - re-send exactly those operations to continue,
		from your own copy of the request (slot meta lines carry the index and tool name only, never the
		arguments). The response also carries a total output budget; over it, remaining slots are Truncated
		the same way. The header names the snapshot=<id> every slot was computed against; pass it back as
		expectSnapshot when you continue so a continuation that would straddle two generations of the
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
	internal async Task<string> ExecuteBatchAsync(
		SolutionModel pinnedModel,
		RoslynInstance instance,
		IReadOnlyDictionary<string, MultiQueryCatalog.OpEntry> catalog,
		IReadOnlyList<MultiQueryOperation> operations,
		string? boundary = null)
	{
		string effectiveBoundary = boundary ?? Guid.NewGuid().ToString("N");
		var envelope = new System.Text.StringBuilder();
		envelope.Append("operations=").Append(operations.Count).Append('\n');
		envelope.Append("snapshot=").Append(pinnedModel.Id.ToString("N")).Append('\n');
		envelope.Append("boundary=").Append(effectiveBoundary).Append('\n');

		int truncatedCount = 0;
		int totalChars = 0;
		for (int index = 0; index < operations.Count; index++)
		{
			// The two truncation triggers, one code path (Decisions 1 and 5): an op beyond the count limit,
			// or the first op that would cross the output budget, is not executed - its slot carries a whole
			// error=Truncated block naming why, so the caller can re-send exactly these operations.
			string body;
			if (index >= MaxOperations)
			{
				truncatedCount++;
				body = OutlineError.Format(
					Error.Truncated($"At most {MaxOperations} operations per batch. This operation was not run; re-send it (with its original arguments) to continue."),
					pinnedModel.Status);
			}
			else if (totalChars >= MaxTotalOutputChars)
			{
				truncatedCount++;
				body = OutlineError.Format(
					Error.Truncated("Response budget exhausted. This operation was not run; re-send it (with its original arguments) to continue."),
					pinnedModel.Status);
			}
			else
			{
				body = await ExecuteOperationAsync(pinnedModel, instance, catalog, operations[index]).ConfigureAwait(false);
				totalChars += body.Length;
			}

			// A body may not end with a newline (any slot body is the tool's verbatim output), so the
			// delimiter line always carries its own leading '\n' - when the body already ended with one this
			// renders the blank line the envelope format shows; when it did not, it terminates the line.
			envelope.Append('\n').Append("--").Append(effectiveBoundary).Append('\n');
			envelope.Append("slot=").Append(index + 1).Append(" tool=").Append(operations[index].Tool).Append('\n');
			envelope.Append('\n');
			envelope.Append(body);
		}

		if (truncatedCount > 0)
			envelope.Append("truncatedSlots=").Append(truncatedCount).Append('\n');
		envelope.Append('\n').Append("--").Append(effectiveBoundary).Append("--").Append('\n');

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
			return await MultiQueryCatalog.InvokeCoreAsync(
				Provider,
				entry,
				pinnedModel,
				pinnedInstance,
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
