namespace Morris.Roslynk.Infrastructure.Watching;

/// <summary>
/// The OS-facing half of the watcher: owns the <see cref="FileSystemWatcher"/>s over the directories that
/// back the loaded documents and build files, debounces their noisy event bursts (editors save in 2-3
/// events), and hands each batch of changed paths to <see cref="SolutionFileSync"/>. Deliberately thin and
/// best-effort; a dropped, late, or duplicated event only costs freshness, never safety, because the
/// apply pipeline's stale-write guard is what protects the user.
/// </summary>
public sealed class SolutionFileWatcher : IDisposable
{
	private const int DebounceMilliseconds = 250;

	private readonly SolutionFileSync Sync;
	private readonly List<FileSystemWatcher> Watchers = [];
	private readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly object Gate = new();
	private readonly Timer Debounce;
	private bool Disposed;

	public SolutionFileWatcher(SolutionFileSync sync)
	{
		Sync = sync ?? throw new ArgumentNullException(nameof(sync));
		Debounce = new Timer(_ => Flush(), state: null, Timeout.Infinite, Timeout.Infinite);

		foreach (WatchTarget target in sync.WatchTargets())
			TryWatch(target);
	}

	private void TryWatch(WatchTarget target)
	{
		if (!Directory.Exists(target.Directory))
			return;

		var watcher = new FileSystemWatcher(target.Directory)
		{
			IncludeSubdirectories = target.Recursive,
			// DirectoryName: a directory created, moved in, renamed or moved out raises one event for itself and
			// none for the files it holds.
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
		};

		watcher.Changed += OnChanged;
		watcher.Created += OnChanged;
		watcher.Deleted += OnChanged;
		watcher.Renamed += OnRenamed;
		watcher.EnableRaisingEvents = true;

		Watchers.Add(watcher);
	}

	private void OnChanged(object sender, FileSystemEventArgs e)
	{
		// A directory's own Changed event only says a file inside it was written, which that file's event covers.
		if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
			return;

		Queue(e.FullPath);
	}

	private void OnRenamed(object sender, RenamedEventArgs e)
	{
		Queue(e.OldFullPath);
		Queue(e.FullPath);
	}

	private void Queue(string path)
	{
		lock (Gate)
		{
			if (Disposed)
				return;
			Pending.Add(path);
			Debounce.Change(DebounceMilliseconds, Timeout.Infinite);
		}
	}

	private void Flush()
	{
		string[] paths;
		lock (Gate)
		{
			if (Disposed)
				return;
			paths = [.. Pending];
			Pending.Clear();
		}

		_ = HandleAsync(paths);
	}

	private async Task HandleAsync(IReadOnlyList<string> paths)
	{
		try
		{
			await Sync.OnFilesChangedAsync(paths);
		}
		catch
		{
			// Freshness is best-effort; the stale-write guard protects correctness regardless.
		}
	}

	public void Dispose()
	{
		lock (Gate)
		{
			if (Disposed)
				return;
			Disposed = true;
		}

		Debounce.Dispose();
		foreach (FileSystemWatcher watcher in Watchers)
		{
			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}
	}
}
