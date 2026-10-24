using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.QueryExecutionTests;

/// <summary>
/// The shared read guard, proven deterministically: every test advances, republishes or cancels the
/// instance from INSIDE the query callback, so nothing depends on a sleep or a timing guess.
/// </summary>
public class QueryExecutionTests
{
	[Fact]
	public async Task WhenTheSnapshotIsUnchanged_ThenTheQueryResultIsReturnedVerbatim()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await LoadedAsync(registry);

		string result = await QueryExecution.QueryAsync(instance, (_, _) => Task.FromResult("body=unchanged\n"));

		Assert.Equal("body=unchanged\n", result);
	}

	[Fact]
	public async Task WhenTheSolutionAdvancesMidQuery_ThenTheResultIsStaleAndNamesTheCurrentSnapshot()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await LoadedAsync(registry);

		string result = await QueryExecution.QueryAsync(instance, (model, _) =>
		{
			// A content change published while the query is "running": a different Solution instance.
			Solution advanced = model.Solution!
				.Projects.First()
				.AddDocument("Added.cs", "namespace Added; public class Added { }")
				.Project.Solution;
			instance.AdvanceTo(advanced);
			return Task.FromResult("body=stale\n");
		});

		Assert.StartsWith("error=Stale", result);
		Assert.Contains(QueryExecution.ChangedMessage, result);
		Assert.Contains($"snapshot={instance.CurrentModel.Id.ToString("N")}", result);
		Assert.DoesNotContain("body=stale", result);
	}

	[Fact]
	public async Task WhenOnlyThePublicationIdChanges_ThenTheQueryResultIsStillReturned()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await LoadedAsync(registry);
		Guid publishedId = Guid.Empty;

		string result = await QueryExecution.QueryAsync(instance, (model, _) =>
		{
			publishedId = model.Id;

			// The same Solution instance republished under a fresh publication id - a status-only
			// generation (Updating, a diagnostics build's Loading/Ready, a failed write's Ready, a no-op
			// fold). The content did not change, so the guard must keep the result.
			instance.AdvanceTo(model.Solution!);
			return Task.FromResult("body=kept\n");
		});

		Assert.Equal("body=kept\n", result);
		Assert.NotEqual(publishedId, instance.CurrentModel.Id);
	}

	[Fact]
	public async Task WhenTheQueryIsCancelled_ThenItReportsStaleWithTheCancelledMessage()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await LoadedAsync(registry);
		using var cancellation = new CancellationTokenSource();

		string result = await QueryExecution.QueryAsync(
			instance,
			(_, token) =>
			{
				cancellation.Cancel();
				token.ThrowIfCancellationRequested();
				return Task.FromResult("body=never\n");
			},
			cancellation.Token);

		Assert.StartsWith("error=Stale", result);
		Assert.Contains(QueryExecution.CancelledMessage, result);
		Assert.Contains("snapshot=", result);
		Assert.DoesNotContain("body=never", result);
	}

	[Fact]
	public async Task WhileTheSolutionIsIndexing_ThenTheQuerysOwnIndexingResultPassesThrough()
	{
		using var registry = new InstanceRegistry();

		// A path that cannot load faults its snapshot; while (or after) loading there is no Solution, and
		// the query's own Indexing result already tells the caller to retry - the guard must not overwrite it.
		string missing = TestSolutions.Simple.Replace(".slnx", ".missing.slnx");
		RoslynInstance instance = registry.GetOrBegin(missing);

		string result = await QueryExecution.QueryAsync(instance, (model, _) =>
		{
			Assert.Null(model.Solution);
			return Task.FromResult(OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status));
		});

		Assert.StartsWith("error=Indexing", result);
		Assert.DoesNotContain("snapshot=", result);
		Assert.DoesNotContain("error=Stale", result);
	}

	private static async Task<RoslynInstance> LoadedAsync(InstanceRegistry registry)
	{
		await registry.GetOrAddAsync(TestSolutions.Simple);
		return await registry.GetOrBeginAsync(TestSolutions.Simple);
	}
}
