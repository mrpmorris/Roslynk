using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Base class for a test that mutates a solution on disk. The test gets its own scratch copy of a
/// fixture under <c>%TEMP%/roslynk-tests/&lt;guid&gt;</c>, its own <see cref="InstanceRegistry"/>, and the
/// copy is deleted when the test finishes.
/// <para>
/// A class may use several different fixtures, so the copy is created by calling
/// <see cref="ScratchAsync"/> with the <c>TestSolutions.CreateScratch...</c> helper it needs, rather than
/// by overriding a per-class method. The first call creates the Registry; a test that never calls it
/// leaves nothing behind.
/// </para>
/// <para>
/// Teardown order matters: the Registry is disposed first, which disposes the instance, its workspace
/// and the MSBuild build host that has the copied project files open. Only then can the directory be
/// removed. Deletion is retried because the build host shuts down asynchronously and can briefly hold a
/// file after <see cref="InstanceRegistry.Dispose"/> returns.
/// </para>
/// </summary>
public abstract class MutatingTestBase : IAsyncLifetime
{
	private const int DeleteAttempts = 5;
	private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(120);

	private readonly List<string> ScratchRoots = [];
	private InstanceRegistry? RegistryField;

	/// <summary>
	/// The GUID directory a scratch copy lives under. <c>CreateScratch</c> returns
	/// <c>%TEMP%/roslynk-tests/&lt;guid&gt;/&lt;fixture&gt;/&lt;solutionFile&gt;</c>, and a fixture whose solution sits in a
	/// subfolder (MultiTargetSolution uses <c>src/</c>) is one level deeper, so the depth is not fixed.
	/// Walking up to the child of the <c>roslynk-tests</c> directory is what identifies it reliably.
	/// </summary>
	private static string RunRootOf(string solutionPath)
	{
		string rootName = "roslynk-tests";
		string? current = Path.GetDirectoryName(solutionPath);
		while (current is not null)
		{
			string? parent = Path.GetDirectoryName(current);
			if (parent is not null && string.Equals(Path.GetFileName(parent), rootName, StringComparison.OrdinalIgnoreCase))
				return current;
			current = parent;
		}

		// Not under the shared root (a caller-supplied directory): delete just its own folder.
		return Path.GetDirectoryName(solutionPath)!;
	}

	/// <summary>The registry holding this test's loaded solutions; created on the first <see cref="ScratchAsync"/>.</summary>
	protected InstanceRegistry Registry =>
		RegistryField ?? throw new InvalidOperationException("Call ScratchAsync or Scratch before using the registry.");

	/// <summary>
	/// Creates a scratch copy of a fixture with <paramref name="create"/>, loads it into this test's private
	/// Registry, and returns the loaded instance and the copy's solution path.
	/// </summary>
	protected async Task<(RoslynInstance Instance, string SolutionPath)> ScratchAsync(Func<string> create)
	{
		string solutionPath = create();
		ScratchRoots.Add(RunRootOf(solutionPath));

		RegistryField ??= new InstanceRegistry();
		RoslynInstance instance = await RegistryField.GetOrAddAsync(solutionPath);
		return (instance, solutionPath);
	}

	/// <summary>Creates a scratch copy without loading it; the registry is still created for a later load.</summary>
	protected string Scratch(Func<string> create)
	{
		string solutionPath = create();
		ScratchRoots.Add(RunRootOf(solutionPath));
		RegistryField ??= new InstanceRegistry();
		return solutionPath;
	}

	public Task InitializeAsync() => Task.CompletedTask;

	/// <summary>
	/// Disposes the Registry so the workspace releases its file handles, then deletes every scratch copy
	/// this test created. A failure to delete is not a test failure: the directories are GUID-named and
	/// never reused, and the next run's startup sweep removes anything left behind.
	/// </summary>
	public async Task DisposeAsync()
	{
		RegistryField?.Dispose();
		RegistryField = null;

		foreach (string root in ScratchRoots)
			await DeleteWithRetryAsync(root);
	}

	private static async Task DeleteWithRetryAsync(string root)
	{
		for (int attempt = 1; attempt <= DeleteAttempts; attempt++)
		{
			try
			{
				if (!Directory.Exists(root))
					return;
				Directory.Delete(root, recursive: true);
				if (!Directory.Exists(root))
					return;
			}
			catch (IOException) when (attempt < DeleteAttempts)
			{
				await Task.Delay(DeleteRetryDelay);
			}
			catch (UnauthorizedAccessException) when (attempt < DeleteAttempts)
			{
				await Task.Delay(DeleteRetryDelay);
			}
			catch (IOException)
			{
				return;
			}
			catch (UnauthorizedAccessException)
			{
				return;
			}

			// A recursive delete can remove every child and leave the root behind when a handle is open
			// on it; retrying clears that up once the handle is released.
			if (attempt < DeleteAttempts)
				await Task.Delay(DeleteRetryDelay);
		}
	}
}
