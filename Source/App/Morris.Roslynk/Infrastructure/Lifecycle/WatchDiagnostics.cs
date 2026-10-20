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
	/// How many distinct messages are kept. A bound on memory only: get_solution_status applies its own listing
	/// limit, and counts what this bound drops as truncated.
	/// </summary>
	public const int MaxMessages = 100;

	private readonly Lock Gate = new();
	private readonly Dictionary<string, string> MessagesByError = new(StringComparer.Ordinal);
	private int DroppedField;

	/// <summary>The recorded messages, each as '<c>error (directory)</c>'.</summary>
	public IReadOnlyList<string> Messages
	{
		get
		{
			lock (Gate)
				return MessagesByError.Values.ToArray();
		}
	}

	/// <summary>How many distinct messages were not kept because <see cref="MaxMessages"/> was reached.</summary>
	public int Dropped
	{
		get
		{
			lock (Gate)
				return DroppedField;
		}
	}

	/// <summary>Whether an error with this message is already recorded.</summary>
	public bool Contains(string error)
	{
		lock (Gate)
			return MessagesByError.ContainsKey(error);
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
				DroppedField++;
		}
	}
}
