using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins;

/// <summary>One read of a solution's model.</summary>
/// <param name="ModelId">
/// The publication generation: new each time the daemon publishes a model, including when only its status
/// changes (a diagnostics build, an edit that changed nothing). It is not a content version. To tell whether
/// the solution itself changed, compare <paramref name="Model"/> with the previous one by reference: Roslyn
/// solutions are immutable, so the same instance means the same content.
/// </param>
/// <param name="Status">The model's load state.</param>
/// <param name="Model">The Roslyn solution; null before the first load finishes, and when the solution is faulted.</param>
/// <param name="ErrorMessage">Why the solution is <see cref="SolutionStatus.Faulted"/>; otherwise null.</param>
public sealed record GetModelResponse(
	Guid ModelId,
	SolutionStatus Status,
	Solution? Model,
	string? ErrorMessage);
