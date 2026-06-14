namespace Morris.Roslynk.Infrastructure.Common;

/// <summary>
/// The single error shape returned by every tool.
/// </summary>
public sealed record ErrorEnvelope(
	string Code,
	string Message,
	SnapshotId? CurrentSnapshotId = null,
	string? Details = null);
