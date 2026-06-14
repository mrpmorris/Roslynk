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
