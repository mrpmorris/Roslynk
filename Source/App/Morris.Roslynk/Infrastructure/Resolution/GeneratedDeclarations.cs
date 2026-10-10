using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Declarations that live in source-generated documents, which Roslyn's declaration search can miss:
/// <see cref="SymbolFinder.FindDeclarationsAsync"/> and <see cref="SymbolFinder.FindSourceDeclarationsAsync"/>
/// first ask a syntactic index built from a project's regular documents (<c>Project.Documents</c>) whether the
/// name occurs at all, and skip the project when it does not. A declaration only a generator emits is therefore
/// found only when hand-written code of the same project happens to mention its name. The compilation holds the
/// generated trees, so asking it directly finds them.
/// </summary>
/// <remarks>
/// Requesting the compilation runs the project's generators, so callers use this only where the cost is
/// acceptable: a resolution miss, a bare name, or a suggestion list. Razor's generated documents are
/// ordinary model documents, already indexed by the declaration search, and are never returned here.
/// </remarks>
internal static class GeneratedDeclarations
{
	/// <summary>
	/// Whether the project can have source-generated documents, decided without running its generators
	/// (asking for the generated documents themselves would build the compilation). Generators arrive as
	/// analyzer references — and Roslynk's shadow loader has already forced their assembly load — so a
	/// project without any is skipped for free.
	/// </summary>
	public static bool MayHaveGeneratedDocuments(Project project) =>
		project.SupportsCompilation
		&& project.AnalyzerReferences.Any(reference => !reference.GetGenerators(project.Language).IsDefaultOrEmpty);

	/// <summary>The project's declarations named exactly <paramref name="name"/> that a generator emitted.</summary>
	public static Task<IReadOnlyList<ISymbol>> FindAsync(Project project, string name, CancellationToken cancellationToken = default) =>
		FindAsync(project, compilation => compilation.GetSymbolsWithName(name, SymbolFilter.All, cancellationToken), cancellationToken);

	/// <summary>The project's declarations whose name satisfies <paramref name="predicate"/> that a generator emitted.</summary>
	public static Task<IReadOnlyList<ISymbol>> FindAsync(Project project, Func<string, bool> predicate, SymbolFilter filter, CancellationToken cancellationToken = default) =>
		FindAsync(project, compilation => compilation.GetSymbolsWithName(predicate, filter, cancellationToken), cancellationToken);

	private static async Task<IReadOnlyList<ISymbol>> FindAsync(Project project, Func<Compilation, IEnumerable<ISymbol>> lookup, CancellationToken cancellationToken)
	{
		if (!MayHaveGeneratedDocuments(project) || await project.GetCompilationAsync(cancellationToken) is not Compilation compilation)
			return [];

		// Hand-written declarations are left to the declaration search: a project declaring the name in a
		// regular document always passes that search's pre-filter, so only generated-only symbols are
		// missing. Filtering keeps the helper's contract crisp (and reusable for search_symbols), even
		// though the seen set would dedupe a re-surfaced hand-written symbol anyway.
		Solution solution = project.Solution;
		return lookup(compilation)
			.Where(symbol => symbol.DeclaringSyntaxReferences.Any(reference => GeneratedSource.IsGenerated(solution, reference.SyntaxTree)))
			.ToArray();
	}
}
