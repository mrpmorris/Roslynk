using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Razor;
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
		SyntaxTree? tree = location?.SourceTree;

		// The leaf's line and column come from the display span, which follows a #line mapping into the
		// .razor/.cshtml source, while the tree's FilePath names the virtual generated document. Taking the
		// file from the tree would name a path that does not exist on disk, so the path comes from the same
		// span as the position. A location with no mapped path has no Razor origin (BuildRenderTree's
		// generated body) and keeps its tree path. The project grouping still comes from the tree.
		FileLinePositionSpan? span = location is null ? null : location.GetDisplaySpan();
		string? path = null;
		if (span is FileLinePositionSpan display)
			path = string.IsNullOrEmpty(display.Path) ? tree?.FilePath : display.Path;

		string file = path is null
			? MetadataBucket
			: GeneratedSource.MarkPath(SolutionRelativePath.Of(solutionDirectory, path)!, solution, tree);

		SymbolNode start = tree is not null && ProjectName.Of(solution, tree) is string project
			? root.Child(project)
			: root;

		SymbolNode node = start.ChildPath(file).Child(NamespaceOf(symbol));

		var parents = new List<INamedTypeSymbol>();
		for (INamedTypeSymbol? containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
			parents.Insert(0, containing);

		foreach (INamedTypeSymbol parent in parents)
			node = node.Child($"{SymbolKindText.Of(parent)},{OutlineBuilder.Field(parent.Name)}");

		// A local function nests under the member (and any outer local functions) declaring it.
		foreach (ISymbol container in LocalFunctions.ContainerChain(symbol))
			node = node.Child($"{SymbolKindText.Of(container)},{OutlineBuilder.Field(container.Name)}");

		SymbolNode leaf = node.Child($"{SymbolKindText.Of(symbol)},{OutlineBuilder.Field(symbol.Name)}");

		if (span is FileLinePositionSpan located)
			leaf.AddLocation(
				located.StartLinePosition.Line + 1,
				located.StartLinePosition.Character + 1,
				located.EndLinePosition.Line + 1,
				located.EndLinePosition.Character + 1);
	}

	internal static string NamespaceOf(ISymbol symbol)
	{
		INamespaceSymbol? containing = symbol.ContainingNamespace;
		return containing is null || containing.IsGlobalNamespace ? GlobalNamespace : containing.ToDisplayString();
	}
}
