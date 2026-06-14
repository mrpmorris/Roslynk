using System.Collections.Concurrent;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The process-singleton that holds one <see cref="RoslynInstance"/> per solution, shared across
/// sessions. Concurrent requests for the same solution load it exactly once (the <see cref="Lazy{T}"/>
/// guards the load); requests for different solutions get independent instances.
/// </summary>
public sealed class InstanceRegistry : IDisposable
{
	private readonly ConcurrentDictionary<SolutionKey, Lazy<Task<RoslynInstance>>> Instances = new();

	public Task<RoslynInstance> GetOrAddAsync(string solutionPath)
	{
		SolutionKey key = SolutionKey.For(solutionPath);
		Lazy<Task<RoslynInstance>> lazy = Instances.GetOrAdd(
			key,
			static k => new Lazy<Task<RoslynInstance>>(() => LoadAsync(k)));
		return lazy.Value;
	}

	private static async Task<RoslynInstance> LoadAsync(SolutionKey key)
	{
		SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(key.Path);
		return new RoslynInstance(key, workspace);
	}

	/// <summary>Removes a loaded solution and disposes it. Returns false if it was not loaded.</summary>
	public bool TryClose(string solutionPath)
	{
		if (!Instances.TryRemove(SolutionKey.For(solutionPath), out Lazy<Task<RoslynInstance>>? lazy))
			return false;

		if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
			lazy.Value.Result.Dispose();

		return true;
	}

	/// <summary>The instances whose load has completed successfully.</summary>
	public IReadOnlyList<RoslynInstance> LoadedInstances()
	{
		var loaded = new List<RoslynInstance>();
		foreach (Lazy<Task<RoslynInstance>> lazy in Instances.Values)
		{
			if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
				loaded.Add(lazy.Value.Result);
		}

		return loaded;
	}

	/// <summary>Closes a solution if loaded and loads it fresh from disk.</summary>
	public async Task<RoslynInstance> ReloadAsync(string solutionPath)
	{
		TryClose(solutionPath);
		return await GetOrAddAsync(solutionPath);
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
		foreach (Lazy<Task<RoslynInstance>> lazy in Instances.Values)
		{
			if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
				lazy.Value.Result.Dispose();
		}

		Instances.Clear();
	}
}
