using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The shared snapshot guard behind every read-only tool: capture one <see cref="SolutionModel"/> through
/// <see cref="RoslynInstance.ReadModelAsync"/>, run the whole query against that captured snapshot, then
/// refuse the result if the instance has published a different <see cref="Solution"/> meanwhile. A result
/// computed against a superseded generation would silently describe a different version of the solution,
/// so it becomes a <see cref="ErrorCode.Stale"/> retry response instead. Write tools stay on the write
/// pipeline, where <see cref="WriteResult"/> and the stale-write guards own publication safety.
/// </summary>
/// <remarks>
/// The generation comparison is reference equality of the immutable Roslyn <see cref="Solution"/>, not
/// <see cref="SolutionModel.Id"/>. Every content change (watcher fold, write publication, rebuild, reload)
/// produces a new <see cref="Solution"/> instance, while status-only publications - <c>Updating</c> during a
/// write, <c>Loading</c> during a diagnostics build, <c>Ready</c> after a failed write, or a no-op fold -
/// carry the very same instance. Keying on <see cref="SolutionModel.Id"/> would make a query that triggers
/// diagnostics invalidate itself, and would spuriously fail every concurrent query during a write's
/// <c>Updating</c> phase.
/// </remarks>
internal static class QueryExecution
{
	/// <summary>The message a query's caller receives when a newer solution superseded the one it read.</summary>
	internal const string ChangedMessage =
		"The solution changed while this query ran, so its result would describe a superseded snapshot. Re-run the query.";

	/// <summary>The message a query's caller receives when the query was cancelled before it completed.</summary>
	internal const string CancelledMessage =
		"The query was cancelled before it completed. Re-run the query.";

	/// <summary>
	/// Runs <paramref name="query"/> against the model captured at entry and returns its result only while
	/// that model still describes the current solution. A model with no snapshot (the initial load is still
	/// running) passes the query's own <c>Indexing</c> result through unchanged, because it already tells the
	/// caller to retry. Cancellation of the query is reported as the same stale/retry shape rather than as an
	/// exception: the work produced no complete result, so the caller must re-run it.
	/// </summary>
	/// <exception cref="OperationCanceledException">The model could not be captured because
	/// <paramref name="cancellationToken"/> was already cancelled.</exception>
	internal static async Task<string> QueryAsync(
		RoslynInstance instance,
		Func<SolutionModel, CancellationToken, Task<string>> query,
		CancellationToken cancellationToken = default)
	{
		if (instance is null)
			throw new ArgumentNullException(nameof(instance));
		if (query is null)
			throw new ArgumentNullException(nameof(query));

		SolutionModel model = await instance.ReadModelAsync(cancellationToken).ConfigureAwait(false);

		string result;
		try
		{
			result = await query(model, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return Stale(instance, CancelledMessage);
		}

		// Still indexing: there is no generation to compare, and the query's own Indexing result already
		// carries the retry guidance.
		if (model.Solution is null)
			return result;

		if (!ReferenceEquals(instance.CurrentModel.Solution, model.Solution))
			return Stale(instance, ChangedMessage);

		return result;
	}

	/// <summary>
	/// The standard stale/retry response: the shared error block plus the current <c>snapshot=</c> id, the
	/// same shape <c>multi_query</c> uses for an <c>expectSnapshot</c> mismatch, so a caller can see exactly
	/// which generation its result would have been stitched onto.
	/// </summary>
	private static string Stale(RoslynInstance instance, string message)
	{
		SolutionModel current = instance.CurrentModel;
		return OutlineError.Format(Error.Stale(message), current.Status)
			+ $"snapshot={current.Id.ToString("N")}\n";
	}
}
