using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Base class for a test that mutates a solution on disk. The test gets its own scratch copy of a
/// fixture under <c>%TEMP%/roslynk-tests/&lt;guid&gt;</c>, its own <see cref="InstanceRegistry"/>, and the
/// copy is deleted when the test finishes.
/// <para>
/// A class may use several different fixtures, so the copy is created by calling
/// <see cref="ScratchAsync"/> with the <c>TestSolutions.CreateScratch...</c> helper it needs, rather than
/// by overriding a per-class method. <see cref="Registry"/> is created on first use, so a test that only
/// asserts instance state against a committed fixture can use the base class too.
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
	private readonly List<string> ScratchRoots = [];
	private InstanceRegistry? RegistryField;

	/// <summary>
	/// The registry holding this test's loaded solutions. It is created on first use and disposed when the
	/// test finishes, so a test that asserts instance state against a committed fixture can use it without
	/// needing a scratch copy.
	/// </summary>
	protected InstanceRegistry Registry => RegistryField ??= new InstanceRegistry();

	/// <summary>
	/// Creates a scratch copy of a fixture with <paramref name="create"/>, loads it into this test's private
	/// Registry, and returns the loaded instance and the copy's solution path.
	/// </summary>
	protected async Task<(RoslynInstance Instance, string SolutionPath)> ScratchAsync(Func<string> create)
	{
		string solutionPath = create();
		ScratchRoots.Add(ScratchDirectories.RootOf(solutionPath));

		RoslynInstance instance = await Registry.GetOrAddAsync(solutionPath);
		return (instance, solutionPath);
	}

	/// <summary>Creates a scratch copy without loading it; a later <see cref="Registry"/> load picks it up.</summary>
	protected string Scratch(Func<string> create)
	{
		string solutionPath = create();
		ScratchRoots.Add(ScratchDirectories.RootOf(solutionPath));
		return solutionPath;
	}

	/// <summary>
	/// Records a directory this test created outside <see cref="Scratch"/>, so it is deleted with the test.
	/// Use it for a directory a test makes for itself — a moved folder, a synthetic build tree — rather
	/// than for a solution copy.
	/// </summary>
	protected void TrackScratch(string path) => ScratchRoots.Add(ScratchDirectories.RootOf(path));

	public Task InitializeAsync() => Task.CompletedTask;

	/// <summary>
	/// Disposes the Registry so the workspace releases its file handles, then deletes every scratch copy
	/// this test created. A copy that cannot be removed yet is retried in the background by
	/// <see cref="ScratchDirectories"/>; a failure is never a test failure, because the directories are
	/// GUID-named, never reused, and swept at the start of the next run.
	/// </summary>
	public Task DisposeAsync()
	{
		RegistryField?.Dispose();
		RegistryField = null;

		foreach (string root in ScratchRoots)
			ScratchDirectories.Delete(root);

		return Task.CompletedTask;
	}
}
