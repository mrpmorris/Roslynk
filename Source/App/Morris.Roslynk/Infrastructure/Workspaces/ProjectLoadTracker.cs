using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.MSBuild;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Counts the distinct projects an <see cref="MSBuildWorkspace"/> reports while opening a solution, so a
/// load in flight can surface a live "projects loaded so far" count. A project is counted once per file
/// path; reporting the same project for several load operations (evaluate, resolve, build) does not
/// double-count.
/// </summary>
public sealed class ProjectLoadTracker : IProgress<ProjectLoadProgress>
{
	private readonly ConcurrentDictionary<string, byte> Loaded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The number of distinct projects reported so far.</summary>
	public int Count => Loaded.Count;

	/// <summary>Records that a project has been seen.</summary>
	public void MarkLoaded(string filePath)
	{
		if (filePath is null)
			throw new ArgumentNullException(nameof(filePath));

		Loaded.TryAdd(filePath, 0);
	}

	public void Report(ProjectLoadProgress value) => MarkLoaded(value.FilePath);
}
