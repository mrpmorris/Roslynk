using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// The in-process Razor generation for one loaded workspace: for each Razor project, the generator, the last
/// <see cref="GeneratorDriver"/> (Roslyn's incremental cache, so a one-file edit regenerates in milliseconds
/// instead of reloading the solution), the generated documents by hint name, and the Razor compiler's own
/// diagnostics (RZxxxx) from the last run. Lives as long as its <c>SolutionWorkspace</c>; a rebuild starts a
/// fresh one. Mutated only during the load and by queued writes, which never overlap; readers see an
/// immutable snapshot of the entries.
/// </summary>
public sealed class RazorGenerationState
{
	private ImmutableDictionary<ProjectId, Entry> EntriesField = ImmutableDictionary<ProjectId, Entry>.Empty;

	/// <param name="Generator">The project's Razor generator.</param>
	/// <param name="Driver">The driver from the last run, or null before the first (a snapshot-loaded project).</param>
	/// <param name="GeneratedRoot">The folder the generated documents' paths are rooted at.</param>
	/// <param name="Documents">The project's generated documents, keyed by the generator's hint name.</param>
	/// <param name="Diagnostics">The Razor compiler's diagnostics from the last run.</param>
	/// <param name="InputCompilations">
	/// The project's full compilation after the last run, and the generator input derived from it (the same
	/// compilation without the generated documents), so an unchanged compilation reuses the identical input.
	/// </param>
	internal sealed record Entry(
		ISourceGenerator Generator,
		GeneratorDriver? Driver,
		string GeneratedRoot,
		ImmutableDictionary<string, DocumentId> Documents,
		ImmutableArray<Diagnostic> Diagnostics,
		(Compilation Full, Compilation Input)? InputCompilations);

	/// <summary>True when Roslynk generates <paramref name="projectId"/>'s Razor documents itself, so an edit
	/// to its Razor sources can be regenerated in place.</summary>
	public bool Covers(ProjectId projectId) => Volatile.Read(ref EntriesField).ContainsKey(projectId);

	internal Entry? Get(ProjectId projectId) => Volatile.Read(ref EntriesField).GetValueOrDefault(projectId);

	internal void Set(ProjectId projectId, Entry entry) =>
		ImmutableInterlocked.Update(ref EntriesField, entries => entries.SetItem(projectId, entry));

	/// <summary>
	/// The Razor compiler's diagnostics from the last generation of every project in <paramref name="solution"/>.
	/// A multi-targeted project reports the same .razor problem once per target framework, so identical
	/// diagnostics are reported once.
	/// </summary>
	public IReadOnlyList<Diagnostic> Diagnostics(Solution solution)
	{
		var seen = new HashSet<(string, string, FileLinePositionSpan, string)>();
		var result = new List<Diagnostic>();
		foreach ((ProjectId projectId, Entry entry) in Volatile.Read(ref EntriesField))
		{
			if (solution.GetProject(projectId) is null)
				continue;

			foreach (Diagnostic diagnostic in entry.Diagnostics)
			{
				FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
				if (seen.Add((diagnostic.Id, span.Path ?? "", span, diagnostic.GetMessage())))
					result.Add(diagnostic);
			}
		}

		return result;
	}
}
