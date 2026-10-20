namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The errors one file watcher met that a reload would not fix, such as an exhausted inotify watch limit: each
/// distinct message once, naming the first directory it was reported for (one failure such as the watch limit
/// hits every directory). Owned by that watcher, so a replacement starts empty and no record outlives the
/// watcher that made it.
/// </summary>
public sealed class WatchDiagnostics
{
	/// <summary>
	/// How many distinct messages are kept in full. Beyond it only a hash of each further distinct message is
	/// kept, so it is still recognised (<see cref="Contains"/>) and counted once as dropped; its text is gone.
	/// </summary>
	public const int MaxMessages = 100;

	private readonly Lock Gate = new();
	private readonly Dictionary<string, string> MessagesByError = new(StringComparer.Ordinal);
	private readonly HashSet<int> DroppedHashes = [];

	/// <summary>The kept messages, each as '<c>error (directory)</c>', and how many distinct messages were dropped,
	/// read together so the two always describe the same moment.</summary>
	public WatchDiagnosticsSnapshot Snapshot()
	{
		lock (Gate)
			return new WatchDiagnosticsSnapshot(MessagesByError.Values.ToArray(), DroppedHashes.Count);
	}

	/// <summary>Whether an error with this message is already recorded, kept or dropped.</summary>
	public bool Contains(string error)
	{
		lock (Gate)
			return MessagesByError.ContainsKey(error) || DroppedHashes.Contains(StringComparer.Ordinal.GetHashCode(error));
	}

	public void Record(string error, string directory)
	{
		lock (Gate)
		{
			if (MessagesByError.ContainsKey(error))
				return;
			if (MessagesByError.Count < MaxMessages)
				MessagesByError.Add(error, $"{error} ({directory})");
			else
				DroppedHashes.Add(StringComparer.Ordinal.GetHashCode(error));
		}
	}
}

/// <summary>A consistent read of <see cref="WatchDiagnostics"/>.</summary>
public readonly record struct WatchDiagnosticsSnapshot(IReadOnlyList<string> Messages, int Dropped)
{
	public static WatchDiagnosticsSnapshot Empty { get; } = new([], 0);
}
