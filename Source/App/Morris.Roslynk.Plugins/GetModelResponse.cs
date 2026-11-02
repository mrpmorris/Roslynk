using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins;

/// <summary>
/// One snapshot of a solution. <paramref name="ModelId"/> is new each time the daemon publishes a snapshot, so a
/// reader that sees a different one knows the solution changed. <paramref name="Model"/> is null until the first
/// load finishes and when the load failed; <paramref name="ErrorMessage"/> says why it failed.
/// </summary>
public sealed record GetModelResponse(
	Guid ModelId,
	SolutionStatus Status,
	Solution? Model,
	string? ErrorMessage);
