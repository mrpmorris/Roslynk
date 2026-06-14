using System.Collections.Concurrent;
using Morris.Roslynk.Infrastructure.Watching;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The process-singleton that holds one <see cref="RoslynInstance"/> per solution, shared across
/// sessions. Concurrent requests for the same solution create it exactly once (the <see cref="Lazy{T}"/>
/// guards the create); the instance loads in the background, so creation is immediate and callers that
/// need the snapshot await <see cref="RoslynInstance.WaitUntilReadyAsync"/>.
/// </summary>
public sealed class InstanceRegistry : IDisposable
{
	private readonly ConcurrentDictionary<SolutionKey, Lazy<RoslynInstance>> Instances = new();

	/// <summary>
	/// Returns the instance for <paramref name="solutionPath"/>, creating it (and starting its background
	/// load) if needed, and awaits its first load so the caller sees a ready snapshot. A dirty instance —
	/// one a build-file edit invalidated — is reloaded here on its next use.
	/// </summary>
	public async Task<RoslynInstance> GetOrAddAsync(string solutionPath)
	{
		SolutionKey key = SolutionKey.For(solutionPath);
		RoslynInstance instance = GetOrCreate(key);
		await instance.WaitUntilReadyAsync();

		if (instance.IsDirty)
		{
			TryClose(key.Path);
			instance = GetOrCreate(key);
			await instance.WaitUntilReadyAsync();
		}

		instance.Touch();
		return instance;
	}

	/// <summary>
	/// Returns the instance for <paramref name="solutionPath"/>, creating it and starting its background
	/// load if needed, but without awaiting the load — the caller reads <see cref="RoslynInstance.CurrentModel"/>
	/// and gets <see cref="SolutionStatus.Building"/> until the first snapshot is ready.
	/// </summary>
	public RoslynInstance GetOrBegin(string solutionPath)
	{
		SolutionKey key = SolutionKey.For(solutionPath);
		RoslynInstance instance = GetOrCreate(key);
		instance.Touch();
		return instance;
	}

	/// <summary>
	/// Closes solutions not used for at least <paramref name="idleFor"/> as of <paramref name="nowUtc"/>,
	/// so long-running hosts do not hold every solution ever opened in memory. Returns how many were closed.
	/// </summary>
	public int EvictIdle(TimeSpan idleFor, DateTime nowUtc)
	{
		int evicted = 0;
		foreach (RoslynInstance instance in LoadedInstances())
		{
			if (nowUtc - instance.LastAccessedUtc >= idleFor && TryClose(instance.Key.Path))
				evicted++;
		}

		return evicted;
	}

	private RoslynInstance GetOrCreate(SolutionKey key)
	{
		Lazy<RoslynInstance> lazy = Instances.GetOrAdd(
			key,
			k => new Lazy<RoslynInstance>(() => CreateAndBeginLoad(k)));
		return lazy.Value;
	}

	private static RoslynInstance CreateAndBeginLoad(SolutionKey key)
	{
		var instance = new RoslynInstance(key);
		instance.BeginInitialLoad(
			loader: () => SolutionWorkspace.LoadAsync(key.Path),
			onReady: AttachWatcher);
		return instance;
	}

	private static void AttachWatcher(RoslynInstance instance)
	{
		var sync = new SolutionFileSync(instance);
		instance.AttachWatcher(new SolutionFileWatcher(sync));
	}

	/// <summary>Removes a loaded solution and disposes it. Returns false if it was not loaded.</summary>
	public bool TryClose(string solutionPath)
	{
		if (!Instances.TryRemove(SolutionKey.For(solutionPath), out Lazy<RoslynInstance>? lazy))
			return false;

		if (lazy.IsValueCreated)
			lazy.Value.Dispose();

		return true;
	}

	/// <summary>The instances that have been created (each may be loading, ready, or faulted).</summary>
	public IReadOnlyList<RoslynInstance> LoadedInstances()
	{
		var loaded = new List<RoslynInstance>();
		foreach (Lazy<RoslynInstance> lazy in Instances.Values)
		{
			if (lazy.IsValueCreated)
				loaded.Add(lazy.Value);
		}

		return loaded;
	}

	/// <summary>Closes a solution if loaded and loads it fresh from disk, awaiting its first load.</summary>
	public async Task<RoslynInstance> ReloadAsync(string solutionPath)
	{
		TryClose(solutionPath);
		RoslynInstance instance = GetOrCreate(SolutionKey.For(solutionPath));
		await instance.WaitUntilReadyAsync();
		return instance;
	}

	/// <summary>
	/// Rebuilds a solution from disk without blocking: an already-loaded instance keeps serving its current
	/// snapshot as <see cref="SolutionStatus.Building"/> while the fresh one loads in the background, then
	/// swaps it in. A not-yet-loaded instance simply continues its initial load. Returns the instance.
	/// </summary>
	public RoslynInstance BeginReload(string solutionPath)
	{
		SolutionKey key = SolutionKey.For(solutionPath);
		RoslynInstance instance = GetOrCreate(key);

		if (instance.CurrentModel.Solution is not null)
			instance.BeginRebuild(() => SolutionWorkspace.LoadAsync(key.Path), AttachWatcher);

		instance.Touch();
		return instance;
	}

	/// <summary>Closes and disposes every loaded solution. Returns how many were closed.</summary>
	public int ClearAll()
	{
		int closed = 0;
		foreach (SolutionKey key in Instances.Keys.ToArray())
		{
			if (TryClose(key.Path))
				closed++;
		}

		return closed;
	}

	public void Dispose()
	{
		foreach (Lazy<RoslynInstance> lazy in Instances.Values)
		{
			if (lazy.IsValueCreated)
				lazy.Value.Dispose();
		}

		Instances.Clear();
	}
}
