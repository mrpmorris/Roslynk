using Microsoft.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// One host-level solution-event queue and one delivery pump. Publishing is fire-and-forget: the
/// write channel, the write lock and the publication thread — on the initial-load, write, rebuild and
/// fault paths alike — are never occupied by a plugin's handler. The pump dequeues events in
/// publication order and invokes OnNext on every subscription in turn, one event at a time, so events
/// preserve publication order globally and per solution, and a plugin's OnNext is never invoked
/// concurrently with itself. An observer that throws is the plugin's own bug: the exception is caught
/// and recorded, and the pump continues with the next subscription. OnError is never called — the
/// IObserver error channel is unused; OnCompleted remains the subscription's only terminal callback.
///
/// A newly subscribed plugin receives the current state of every solution already known through the
/// same queue: the subscription is installed first, then the initial state is captured and enqueued
/// under the same gate publications enqueue under, so every event already enqueued is reflected in
/// the captured state and a live event published during replay queues behind it in publication order.
/// A plugin may observe a duplicate Ready; nothing is lost and no second delivery path exists.
/// </summary>
public sealed class SolutionEventPublisher : ISolutionPublicationListener
{
	private readonly SemaphoreSlim Gate = new(1, 1);
	private readonly Channel<PendingDelivery> Queue = Channel.CreateUnbounded<PendingDelivery>(
		new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
	private readonly Task Pump;
	private readonly ILogger<SolutionEventPublisher> Logger;
	private List<Subscription> Subscriptions = [];
	private bool Quiesced;
	private TaskCompletionSource Drained = NewDrainSignal();

	public SolutionEventPublisher(ILogger<SolutionEventPublisher>? logger = null)
	{
		Logger = logger ?? NullLogger<SolutionEventPublisher>.Instance;
		Pump = Task.Run(DeliverAsync);
	}

	private sealed record Subscription(string PluginName, IPlugin Plugin);

	private sealed record PendingDelivery(ISolutionEvent? Event, bool Completed);

	private static TaskCompletionSource NewDrainSignal() =>
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	/// The event a published model raises, or null when the publication raises none: the transient
	/// Updating status produces no event (the write's completion is one Ready event), and a Ready
	/// republication of the same snapshot — a failed write restoring the previous model, a no-op
	/// fold — produces no event either.
	/// </summary>
	internal static ISolutionEvent? EventFor(SolutionModel model, SolutionKey key)
	{
		SolutionEventKind? kind = model.Status switch
		{
			SolutionStatus.Building => SolutionEventKind.Building,
			SolutionStatus.Faulted => SolutionEventKind.Failed,
			SolutionStatus.Ready or SolutionStatus.Updating => SolutionEventKind.Ready,
			_ => null,
		};

		if (kind is null)
			return null;

		return new SolutionEvent(
			kind.Value,
			key.SolutionId,
			model.Solution,
			model.Status == SolutionStatus.Faulted ? model.FaultMessage : null);
	}

	/// <summary>
	/// Subscribes a plugin: the subscription is installed first, then the initial state is captured
	/// and enqueued on the same queue live events use. Events already enqueued are reflected in the
	/// captured state; a live event published during replay queues behind it in publication order.
	/// </summary>
	public async Task SubscribeAsync(
		IPlugin plugin,
		string pluginName,
		Func<CancellationToken, Task<IReadOnlyList<ISolutionEvent>>> captureInitialState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		ArgumentNullException.ThrowIfNull(captureInitialState);

		// Install before capturing, so an event enqueued before the replay can never be missed.
		Subscriptions = [.. Subscriptions, new Subscription(pluginName, plugin)];

		await Gate.WaitAsync(cancellationToken);
		try
		{
			// The capture runs under the enqueue gate, with no instance locks taken: every model read
			// is a volatile read, so a publication can never wait on this section.
			foreach (ISolutionEvent solutionEvent in await captureInitialState(cancellationToken))
				Queue.Writer.TryWrite(new PendingDelivery(solutionEvent, Completed: false));
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>
	/// Ends event delivery: publication sources are quiesced, then OnCompleted is enqueued as the
	/// final item — after all pending events, it is every plugin's final callback. Completes once the
	/// pump has delivered every pending event and OnCompleted. A handler that never returns stalls
	/// this wait, the accepted mirror of the delivery stall.
	/// </summary>
	public async Task CompleteAsync()
	{
		await Gate.WaitAsync();
		try
		{
			Quiesced = true;
			Queue.Writer.TryWrite(new PendingDelivery(Event: null, Completed: true));
		}
		finally
		{
			Gate.Release();
		}

		await Drained.Task;
	}

	private async Task DeliverAsync()
	{
		await foreach (PendingDelivery delivery in Queue.Reader.ReadAllAsync())
		{
			if (delivery.Completed)
			{
				// OnCompleted is delivered once, to every subscription, as the final callback.
				List<Subscription> subscriptions = Subscriptions;
				foreach (Subscription subscription in subscriptions)
				{
					try
					{
						subscription.Plugin.OnCompleted();
					}
					catch (Exception exception)
					{
						Logger.LogError(
							exception,
							"Roslynk plugin observer threw from OnCompleted: plugin={Plugin}",
							subscription.PluginName);
					}
				}

				Drained.TrySetResult();
				return;
			}

			// The list snapshot at delivery time; a subscription installed after the event was
			// enqueued always had its initial state captured after that event's publication.
			List<Subscription> observers = Subscriptions;
			foreach (Subscription subscription in observers)
			{
				try
				{
					subscription.Plugin.OnNext(delivery.Event!);
				}
				catch (Exception exception)
				{
					Logger.LogError(
						exception,
						"Roslynk plugin observer threw from OnNext: plugin={Plugin} solutionId={SolutionId} kind={Kind}",
						subscription.PluginName,
						delivery.Event!.SolutionId,
						delivery.Event.Kind);
				}
			}
		}
	}

	/// <summary>Called after a publication swapped the current model in.</summary>
	public void OnPublished(SolutionKey key, SolutionModel newModel, SolutionModel previousModel)
	{
		if (newModel.Status == SolutionStatus.Updating)
			return;

		if (newModel.Status == SolutionStatus.Ready
			&& previousModel.Status is SolutionStatus.Ready or SolutionStatus.Updating
			&& ReferenceEquals(previousModel.Solution, newModel.Solution))
		{
			// A status-only republication: a failed write restoring the previous snapshot, or a
			// no-op write. The published snapshot did not change, so no event is raised.
			return;
		}

		if (EventFor(newModel, key) is { } solutionEvent)
			Publish(solutionEvent);
	}

	/// <summary>Called after an idle eviction closed the solution.</summary>
	public void OnEvicted(SolutionKey key) =>
		Publish(new SolutionEvent(SolutionEventKind.Evicted, key.SolutionId, solution: null, errorMessage: null));

	private void Publish(ISolutionEvent solutionEvent)
	{
		Gate.Wait();
		try
		{
			if (Quiesced)
				return;
			Queue.Writer.TryWrite(new PendingDelivery(solutionEvent, Completed: false));
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>The host's implementation of the plugin-facing event shape.</summary>
	internal sealed class SolutionEvent(
		SolutionEventKind kind,
		string solutionId,
		Solution? solution,
		string? errorMessage) : ISolutionEvent
	{
		public SolutionEventKind Kind { get; } = kind;

		public string SolutionId { get; } = solutionId;

		public Solution? Solution { get; } = solution;

		public string? ErrorMessage { get; } = errorMessage;
	}
}
