using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Finds the symbols a project's source generators declare, which Roslyn's declaration search misses on its
/// own: <c>SymbolFinder.FindSourceDeclarationsAsync</c> pre-filters each project through a declaration index
/// that covers <see cref="Project.Documents"/> only, so a
/// generated declaration surfaces only when a hand-written one in the same project also matches the query. This
/// pass asks the compilation directly — its merged declaration table includes the generated trees — and keeps
/// only symbols declared in a source-generated document, which <see cref="Project.Documents"/> never lists.
/// </summary>
/// <remarks>
/// <see cref="SymbolFilter.All"/> and the exclusions below mirror the post-filter Roslyn applies to its own
/// results (<c>DeclarationFinder.FilterByCriteria</c>: no implicitly declared symbols, no accessors), so
/// generated results keep the same shape as hand-written ones — namespaces included, accessors and the
/// synthesized members of generated records excluded. The probe is free for a project without generators
/// (<c>GetSourceGeneratedDocumentsAsync</c> returns none without building a compilation) and, for one with
/// generators, pays for the very compilation the symbol search needs; it is cached per publication.
/// </remarks>
public static class SourceGeneratedDeclarations
{
	/// <summary>Every matching symbol declared in a source-generated document, project by project in solution order.</summary>
	public static async Task<IReadOnlyList<ISymbol>> FindAsync(Solution solution, Func<string, bool> predicate, CancellationToken cancellationToken = default)
	{
		var found = new List<ISymbol>();
		foreach (Project project in solution.Projects)
			found.AddRange(await FindAsync(project, predicate, cancellationToken).ConfigureAwait(false));

		return found;
	}

	/// <summary>Every matching symbol declared in a source-generated document of <paramref name="project"/>.</summary>
	public static async Task<IReadOnlyList<ISymbol>> FindAsync(Project project, Func<string, bool> predicate, CancellationToken cancellationToken = default)
	{
		List<SourceGeneratedDocument> generatedDocuments = [.. await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)];
		if (generatedDocuments.Count == 0)
			return [];

		Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
		if (compilation is null)
			return [];

		var generatedTrees = new HashSet<SyntaxTree>();
		foreach (SourceGeneratedDocument document in generatedDocuments)
		{
			if (await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is SyntaxTree tree)
				generatedTrees.Add(tree);
		}

		return compilation.GetSymbolsWithName(predicate, SymbolFilter.All, cancellationToken)
			.Where(symbol => !symbol.IsImplicitlyDeclared)
			.Where(symbol => !IsAccessor(symbol))
			.Where(symbol => symbol.DeclaringSyntaxReferences.Any(reference => generatedTrees.Contains(reference.SyntaxTree)))
			.ToList();
	}

	private static bool IsAccessor(ISymbol symbol) =>
		symbol is IMethodSymbol
		{
			MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
				or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
		};
}
