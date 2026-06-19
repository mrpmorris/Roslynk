using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Places a symbol into a <see cref="SymbolNode"/> tree under file -> namespace -> containing type(s) -> the
/// symbol's own short-named leaf, attaching its declaration location to the leaf. Containing types are
/// parent-only lines until a symbol's own leaf lands on them, so a matched type that also has matched members
/// carries both its location and its children. Shared by find_implementations, get_callers and search_symbols.
/// </summary>
public static class SymbolPlacement
{
	public const string MetadataBucket = "<metadata>";
	public const string GlobalNamespace = "<global>";

	public static void Place(SymbolNode root, ISymbol symbol, Solution solution, string? solutionDirectory)
	{
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		string file = location?.SourceTree?.FilePath is string path
			? SolutionRelativePath.Of(solutionDirectory, path)!
			: MetadataBucket;

		SymbolNode start = location?.SourceTree is SyntaxTree tree && ProjectName.Of(solution, tree) is string project
			? root.Child(project)
			: root;

		SymbolNode node = start.ChildPath(file).Child(NamespaceOf(symbol));

		var parents = new List<INamedTypeSymbol>();
		for (INamedTypeSymbol? containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
			parents.Insert(0, containing);

		foreach (INamedTypeSymbol parent in parents)
			node = node.Child($"{SymbolKindText.Of(parent)},{OutlineBuilder.Field(parent.Name)}");

		SymbolNode leaf = node.Child($"{SymbolKindText.Of(symbol)},{OutlineBuilder.Field(symbol.Name)}");

		if (location is not null)
		{
			FileLinePositionSpan span = location.GetLineSpan();
			leaf.AddLocation(
				span.StartLinePosition.Line + 1,
				span.StartLinePosition.Character + 1,
				span.EndLinePosition.Line + 1,
				span.EndLinePosition.Character + 1);
		}
	}

	private static string NamespaceOf(ISymbol symbol)
	{
		INamespaceSymbol? containing = symbol.ContainingNamespace;
		return containing is null || containing.IsGlobalNamespace ? GlobalNamespace : containing.ToDisplayString();
	}
}
