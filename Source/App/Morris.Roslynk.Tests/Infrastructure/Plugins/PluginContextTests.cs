using Microsoft.Extensions.Logging.Abstractions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>
/// UseSolutionAsync tests against real fixtures: the shared registry for read-only outcomes, a
/// scratch copy with a private registry for the superseded-callback retry.
/// </summary>
public class PluginContextTests : MutatingTestBase
{
	private static PluginContext CreateContext(InstanceRegistry registry, PluginShutdownState? shutdown = null) =>
		new(
			registry,
			new LoadWaitTimeout(TimeSpan.FromSeconds(60)),
			new ResponseBudget(80_000),
			NullLoggerFactory.Instance,
			shutdown ?? new PluginShutdownState());

	[Fact]
	public async Task WhenNoSolutionFileExistsAtThePath_ThenNotFoundIsReturnedAndTheCallbackDidNotRun()
	{
		PluginContext context = CreateContext(new InstanceRegistry());
		bool callbackRan = false;

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			"C:/nowhere/missing.slnx",
			(_, _) => { callbackRan = true; return ValueTask.FromResult(1); });

		Assert.Equal(UseSolutionResultState.NotFound, result.State);
		Assert.False(result.IsSuccess);
		Assert.False(callbackRan);
	}

	[Fact]
	public async Task WhenTheSolutionIsReady_ThenTheCallbackRunsAgainstTheSnapshotAndSuccessCarriesBoth()
	{
		InstanceRegistry registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		PluginContext context = CreateContext(registry);

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			TestSolutions.Simple,
			(solutionContext, _) => ValueTask.FromResult(solutionContext.Solution.Projects.Count()));

		Assert.Equal(UseSolutionResultState.Success, result.State);
		Assert.True(result.IsSuccess);
		Assert.Equal(registry.LoadedInstances().Single(instance => instance.Key.FilePath == Path.GetFullPath(TestSolutions.Simple)).CurrentSolution.Projects.Count(), result.Result);
		Assert.NotNull(result.Solution);
		Assert.Equal(result.Solution, result.Solution);
	}

	[Fact]
	public async Task WhenTheSolutionFailedToLoad_ThenFaultedCarriesTheReasonAndTheCallbackDidNotRun()
	{
		// A solution file that is not a solution at all fails its load; Failed is a structural state.
		// Under the shared scratch root, so teardown deletes exactly this directory rather than the
		// whole %TEMP% (RootOf resolves the GUID folder whose parent is roslynk-tests).
		string directory = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		TrackScratch(directory);
		string garbagePath = Path.Combine(directory, "Garbage.slnx");
		await File.WriteAllTextAsync(garbagePath, "this is not a solution file");

		InstanceRegistry registry = new(new LoadWaitTimeout(TimeSpan.FromSeconds(60)));
		PluginContext context = CreateContext(registry);
		bool callbackRan = false;

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			garbagePath,
			(_, _) => { callbackRan = true; return ValueTask.FromResult(1); });

		Assert.Equal(UseSolutionResultState.Faulted, result.State);
		Assert.False(result.IsSuccess);
		Assert.NotNull(result.FaultMessage);
		Assert.False(callbackRan);
	}

	[Fact]
	public async Task WhenReadingThePayloadOfAFaultedResult_ThenItThrowsNamingTheState()
	{
		PluginContext context = CreateContext(new InstanceRegistry());

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			"C:/nowhere/missing.slnx",
			(_, _) => ValueTask.FromResult(1));

		InvalidOperationException solutionError = Assert.Throws<InvalidOperationException>(() => _ = result.Solution);
		InvalidOperationException resultError = Assert.Throws<InvalidOperationException>(() => _ = result.Result);
		Assert.Contains("NotFound", solutionError.Message);
		Assert.Contains("NotFound", resultError.Message);
	}

	[Fact]
	public async Task WhenRoslynkIsShuttingDown_ThenFaultedIsReturnedWithoutReloading()
	{
		var shutdown = new PluginShutdownState();
		shutdown.BeginShutdown();
		PluginContext context = CreateContext(new InstanceRegistry(), shutdown);
		bool callbackRan = false;

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			TestSolutions.Simple,
			(_, _) => { callbackRan = true; return ValueTask.FromResult(1); });

		Assert.Equal(UseSolutionResultState.Faulted, result.State);
		Assert.Contains("shutting down", result.FaultMessage);
		Assert.False(callbackRan);
	}

	[Fact]
	public async Task WhenTheSnapshotIsSupersededDuringTheCallback_ThenTheCallbackRunsAgainAgainstTheFreshSnapshot()
	{
		string solutionPath = Scratch(TestSolutions.CreateScratchSimpleSolution);
		var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.FromSeconds(60)));
		PluginContext context = CreateContext(registry);
		int runs = 0;

		IUseSolutionResult<int> result = await context.UseSolutionAsync(
			solutionPath,
			async (solutionContext, _) =>
			{
				runs++;
				if (runs == 1)
				{
					// A queued write publishes a fresh snapshot while the callback runs; the freshness
					// check detects the supersession and retries the callback against the new one.
					RoslynInstance instance = registry.LoadedInstances().Single();
					await instance.EnqueueWriteAsync((solution, _) =>
						Task.FromResult(new WriteResult(
							solution.WithDocumentText(
								solution.Projects.SelectMany(project => project.Documents).First().Id,
								Microsoft.CodeAnalysis.Text.SourceText.From("// edited")),
							[])));
				}

				return await ValueTask.FromResult(solutionContext.Solution.Projects.Count());
			});

		Assert.True(result.State == UseSolutionResultState.Success, $"STATE={result.State} FAULT={result.FaultMessage} RUNS={runs} EXISTS={File.Exists(solutionPath)} PATH={solutionPath}");
		Assert.Equal(2, runs);
	}
}
