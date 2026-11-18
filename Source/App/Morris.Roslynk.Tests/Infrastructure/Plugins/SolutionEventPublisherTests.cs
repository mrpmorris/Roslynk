using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class SolutionEventPublisherTests
{
	private static readonly SolutionKey Key = SolutionKey.For("C:/work/Demo.sln");

	private static SolutionEventPublisher CreatePublisher(out CapturingLogger<SolutionEventPublisher> logger)
	{
		logger = new CapturingLogger<SolutionEventPublisher>();
		return new SolutionEventPublisher(logger);
	}

	[Fact]
	public async Task WhenAWriteSwapsUpdating_ThenNoEventIsRaised()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		publisher.OnPublished(Key, SolutionModel.Updating(null!), SolutionModel.Loading(null));
		await publisher.CompleteAsync();

		Assert.Empty(observer.Events);
	}

	[Fact]
	public async Task WhenAFailedWriteRepublishesTheSameSnapshot_ThenNoEventIsRaised()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		// A write publishes Updating, then a failed write restores the previous Ready model unchanged.
		publisher.OnPublished(Key, ReadyModel(null!), ReadyModel(null!));
		publisher.OnPublished(Key, SolutionModel.Updating(null!), ReadyModel(null!));
		publisher.OnPublished(Key, ReadyModel(null!), SolutionModel.Updating(null!));
		await publisher.CompleteAsync();

		Assert.Empty(observer.Events);
	}

	[Fact]
	public async Task WhenANewSnapshotIsPublished_ThenAReadyEventCarriesIt()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		publisher.OnPublished(Key, ReadyModel(null!), SolutionModel.Loading(null));
		await publisher.CompleteAsync();

		var @event = Assert.Single(observer.Events);
		Assert.Equal(SolutionEventKind.Ready, @event.Kind);
		Assert.Equal(Key.SolutionId, @event.SolutionId);
	}

	[Fact]
	public async Task WhenALoadFails_ThenTheFailedEventCarriesTheFaultMessage()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		publisher.OnPublished(Key, SolutionModel.Faulted("the SDK is missing"), SolutionModel.Loading(null));
		await publisher.CompleteAsync();

		var @event = Assert.Single(observer.Events);
		Assert.Equal(SolutionEventKind.Failed, @event.Kind);
		Assert.Null(@event.Solution);
		Assert.Equal("the SDK is missing", @event.ErrorMessage);
	}

	[Fact]
	public async Task WhenAnObserverThrows_ThenTheErrorIsRecordedAndThePumpContinues()
	{
		SolutionEventPublisher publisher = CreatePublisher(out CapturingLogger<SolutionEventPublisher> logger);
		var throwing = new RecordingPlugin(throwOnNext: true);
		var healthy = new RecordingPlugin();
		await publisher.SubscribeAsync(throwing, "throwing", EmptyState, CancellationToken.None);
		await publisher.SubscribeAsync(healthy, "healthy", EmptyState, CancellationToken.None);

		publisher.OnPublished(Key, ReadyModel(null!), SolutionModel.Loading(null));
		await publisher.CompleteAsync();

		var error = logger.Records.Single(record => record.Level == LogLevel.Error);
		Assert.Contains("throwing", error.Message);
		Assert.Contains("OnNext", error.Message);
		Assert.Single(healthy.Events); // the pump continued to the next subscription
		Assert.Equal(0, healthy.ErrorCount); // OnError is never called
	}

	[Fact]
	public async Task WhenAPluginSubscribes_ThenTheCapturedStateIsReplayedThroughTheSameQueue()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = new RecordingPlugin();
		await publisher.SubscribeAsync(observer, "observer", _ => Task.FromResult<IReadOnlyList<ISolutionEvent>>(
			[new SolutionEventPublisher.SolutionEvent(SolutionEventKind.Ready, Key.SolutionId, null, null)]), CancellationToken.None);

		await publisher.CompleteAsync();

		var @event = Assert.Single(observer.Events);
		Assert.Equal(SolutionEventKind.Ready, @event.Kind);
		Assert.Equal(Key.SolutionId, @event.SolutionId);
		Assert.Equal(1, observer.CompletedCount);
	}

	[Fact]
	public async Task WhenPublicationHappensDuringReplay_ThenItQueuesBehindTheInitialState()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = new RecordingPlugin();
		await publisher.SubscribeAsync(observer, "observer", _ =>
		{
			// A live publication lands while the replay is enqueuing; the capture holds the enqueue
			// gate, so the live event queues behind the initial state in publication order.
			Task.Run(() => publisher.OnEvicted(Key));
			Thread.Yield();
			return Task.FromResult<IReadOnlyList<ISolutionEvent>>(
				[new SolutionEventPublisher.SolutionEvent(SolutionEventKind.Building, Key.SolutionId, null, null)]);
		}, CancellationToken.None);

		// The live publication waited on the capture's enqueue gate, so it queued behind the initial
		// state; give the pump time to deliver both before quiescing.
		for (int attempt = 0; observer.Events.Count < 2 && attempt < 100; attempt++)
			await Task.Delay(20);

		await publisher.CompleteAsync();

		Assert.Equal([SolutionEventKind.Building, SolutionEventKind.Evicted], observer.Events.Select(@event => @event.Kind).ToArray());
	}

	[Fact]
	public async Task WhenShutdownCompletes_ThenOnCompletedIsTheFinalCallbackAndQuiescedPublicationsRaiseNothing()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		publisher.OnPublished(Key, ReadyModel(null!), SolutionModel.Loading(null));
		await publisher.CompleteAsync();
		publisher.OnPublished(Key, ReadyModel(null!), SolutionModel.Loading(null)); // quiesced: raised after CompleteAsync
		publisher.OnEvicted(Key);

		Assert.Equal([SolutionEventKind.Ready], observer.Events.Select(@event => @event.Kind).ToArray());
		Assert.Equal(1, observer.CompletedCount);
	}

	[Fact]
	public async Task WhenIdleEvictionIsReported_ThenTheEventCarriesNoSolution()
	{
		SolutionEventPublisher publisher = CreatePublisher(out _);
		var observer = await SubscribeAsync(publisher);

		publisher.OnEvicted(Key);
		await publisher.CompleteAsync();

		var @event = Assert.Single(observer.Events);
		Assert.Equal(SolutionEventKind.Evicted, @event.Kind);
		Assert.Null(@event.Solution);
	}

	private static async Task<RecordingPlugin> SubscribeAsync(SolutionEventPublisher publisher)
	{
		var observer = new RecordingPlugin();
		await publisher.SubscribeAsync(observer, "observer", EmptyState, CancellationToken.None);
		return observer;
	}

	private static Task<IReadOnlyList<ISolutionEvent>> EmptyState(CancellationToken _) =>
		Task.FromResult<IReadOnlyList<ISolutionEvent>>([]);

	private static SolutionModel ReadyModel(Solution solution) => SolutionModel.Ready(solution);

	private sealed class RecordingPlugin(bool throwOnNext = false) : IPlugin
	{
		public List<ISolutionEvent> Events { get; } = [];
		public int CompletedCount { get; private set; }
		public int ErrorCount { get; private set; }

		public void OnNext(ISolutionEvent value)
		{
			Events.Add(value);
			if (throwOnNext)
				throw new InvalidOperationException("the plugin's own bug");
		}

		public void OnError(Exception error) => ErrorCount++;

		public void OnCompleted() => CompletedCount++;
	}
}
