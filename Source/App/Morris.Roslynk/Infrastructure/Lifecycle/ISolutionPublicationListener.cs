namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// Observes solution publications and evictions. The plugin host implements this to feed the
/// solution-event pump: every publication raises its event, every idle eviction raises Evicted.
/// Shutdown unloads nothing through here — an unloading solution publishes no event.
/// </summary>
public interface ISolutionPublicationListener
{
	/// <summary>Called after <paramref name="newModel"/> was swapped in as the current model.</summary>
	void OnPublished(SolutionKey key, SolutionModel newModel, SolutionModel previousModel);

	/// <summary>Called after an idle eviction closed the solution.</summary>
	void OnEvicted(SolutionKey key);
}
