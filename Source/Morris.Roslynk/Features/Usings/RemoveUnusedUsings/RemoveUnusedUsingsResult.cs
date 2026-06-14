using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Usings.RemoveUnusedUsings;

/// <summary>
/// The outcome of removing unnecessary using directives. On success <see cref="Applied"/> is true — or
/// false for a checkOnly preview, or a no-op when there was nothing to remove — <see cref="ChangedFiles"/>
/// lists what was, or would be, rewritten and <see cref="RemovedCount"/> the number of directives removed.
/// A document that cannot be resolved is carried as a NotFound on <see cref="ResultBase.Error"/>.
/// </summary>
public sealed record RemoveUnusedUsingsResult : ResultBase
{
	public bool Applied { get; init; }
	public IReadOnlyList<string>? ChangedFiles { get; init; }
	public int RemovedCount { get; init; }
}
