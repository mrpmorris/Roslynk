using System.Collections.Concurrent;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Owns the scratch directories a test run creates under <c>%TEMP%/roslynk-tests</c>.
/// <para>
/// A directory is deleted as soon as the test that owns it finishes. The delete usually succeeds the
/// first time, because the registry is disposed first and that shuts the build host down; when it does
/// not, the directory is retried in the background while the rest of the run continues, so a build host
/// that is a moment slow to release its files does not leave anything behind.
/// </para>
/// <para>
/// What is still there when the run ends is deleted from a process-exit hook: the copies a shared
/// instance keeps open for the whole run (<see cref="TestSolutions.SharedInstanceAsync"/>) and the
/// compiler-argument caches <c>TestCaches</c> hands out. That hook is only reliable for a small number
/// of directories — the runtime stops running the handler after roughly 150 ms — which is why everything
/// else is deleted earlier.
/// </para>
/// <para>
/// If a run is killed outright, the next run's <see cref="ScratchDirectorySweeper"/> clears what is
/// left, so nothing accumulates across runs.
/// </para>
/// </summary>
internal static class ScratchDirectories
{
	private const string RootName = "roslynk-tests";

	/// <summary>
	/// How many times a directory that could not be deleted is retried in the background. A build host
	/// shuts down asynchronously and can hold a copied project file for a while, so this allows about
	/// half a minute before giving up and leaving the directory to the process-exit hook and the sweep.
	/// </summary>
	private const int BackgroundRetries = 120;

	private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

	private static readonly ConcurrentDictionary<string, byte> Tracked = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentQueue<string> Pending = new();
	private static int ExitRegistered;
	private static int RetryWorkerStarted;

	/// <summary>
	/// Records the GUID directory that <paramref name="pathUnderRoot"/> lives in, so it is deleted when
	/// the process exits. The depth below <c>roslynk-tests</c> is not fixed: a fixture whose solution
	/// sits in a subfolder (MultiTargetSolution uses <c>src/</c>) is one level deeper.
	/// </summary>
	public static void Track(string pathUnderRoot)
	{
		string root = RootOf(pathUnderRoot);
		if (IsSharedRoot(root))
			return;  // a path directly under the shared root has no copy of its own

		if (!Tracked.TryAdd(root, 0))
			return;

		if (Interlocked.Exchange(ref ExitRegistered, 1) == 0)
			AppDomain.CurrentDomain.ProcessExit += static (_, _) => DeleteTracked();
	}

	/// <summary>
	/// Deletes the directory <paramref name="pathUnderRoot"/> lives in. Call it after the workspace that
	/// has the copy open has been disposed. A directory that cannot be removed yet is retried in the
	/// background and, failing that, by the process-exit hook and the next run's sweep.
	/// </summary>
	public static void Delete(string pathUnderRoot)
	{
		string root = RootOf(pathUnderRoot);
		if (IsSharedRoot(root))
			return;  // never remove the directory every other test's copies live in

		if (TryDelete(root))
		{
			Tracked.TryRemove(root, out _);
			return;
		}

		Tracked.TryAdd(root, 0);
		Pending.Enqueue(root);

		if (Interlocked.Exchange(ref RetryWorkerStarted, 1) == 0)
			_ = Task.Run(RetryPendingAsync);
	}

	/// <summary>
	/// Retries the directories teardown could not remove while the rest of the run continues, so a build
	/// host that is a moment slow to release its files does not leave anything behind. Stops once the
	/// queue is empty rather than outliving the work it was started for.
	/// </summary>
	private static async Task RetryPendingAsync()
	{
		try
		{
			for (int attempt = 0; attempt < BackgroundRetries; attempt++)
			{
				if (Pending.IsEmpty)
					return;

				await Task.Delay(RetryDelay);

				int count = Pending.Count;
				for (int i = 0; i < count; i++)
				{
					if (!Pending.TryDequeue(out string? root))
						break;

					if (TryDelete(root))
						Tracked.TryRemove(root, out _);
					else
						Pending.Enqueue(root);
				}
			}
		}
		finally
		{
			// Let a later failed delete start a fresh worker rather than queueing into nothing.
			Interlocked.Exchange(ref RetryWorkerStarted, 0);
		}
	}

	/// <summary>
	/// The GUID directory a scratch path lives under. Accepts either a file inside the copy or the copy's
	/// directory itself, because callers pass both. A path that is not under the shared root (a
	/// caller-supplied directory) is its own root.
	/// </summary>
	public static string RootOf(string path)
	{
		string? directory = Path.GetDirectoryName(path);

		// `path` may already be the GUID directory rather than a file inside it.
		if (directory is not null && IsSharedRoot(directory))
			return path;

		string? current = directory;
		while (current is not null)
		{
			string? parent = Path.GetDirectoryName(current);
			if (parent is not null && IsSharedRoot(parent))
				return current;
			current = parent;
		}

		return directory!;
	}

	private static bool IsSharedRoot(string path) =>
		string.Equals(Path.GetFileName(path), RootName, StringComparison.OrdinalIgnoreCase);

	private static void DeleteTracked()
	{
		if (Tracked.IsEmpty)
			return;

		// Parallel because the runtime gives the handler only a short window: a few directories in
		// sequence would already spend most of it.
		Parallel.ForEach(Tracked.Keys, static root =>
		{
			if (!IsSharedRoot(root))
				TryDelete(root);
		});
	}

	private static bool TryDelete(string root)
	{
		try
		{
			if (!Directory.Exists(root))
				return true;
			Directory.Delete(root, recursive: true);
			return !Directory.Exists(root);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}
}
