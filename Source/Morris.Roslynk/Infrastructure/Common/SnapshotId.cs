namespace Morris.Roslynk.Infrastructure.Common;

/// <summary>
/// A monotonic label for a workspace snapshot, stamped on every response so a caller can tell
/// whether the model has moved on. The authoritative write-time check is the per-file content
/// hash, not this value.
/// </summary>
public readonly record struct SnapshotId(long Value);
