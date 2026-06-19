using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Resolves the declaration chain that lexically contains a source location: the namespace, then the
/// outermost containing type down to the enclosing member or type. Used to nest reference locations under
/// where they appear. Falls back to a synthetic bucket for code with no enclosing declaration (top-level
/// statements, usings).
/// </summary>
public static class EnclosingDeclaration
{
	private static readonly EnclosingPath None =
		new("<global>", [new EnclosingSegment("<file>", "<top-level>")]);

	public static async Task<EnclosingPath> ResolveAsync(
		Solution solution, Location location, CancellationToken cancellationToken = default)
	{
		if (location.SourceTree is null)
			return None;

		Document? document = solution.GetDocument(location.SourceTree);
		if (document is null)
			return None;

		SyntaxNode root = await location.SourceTree.GetRootAsync(cancellationToken);
		MemberDeclarationSyntax? declaration = root
			.FindNode(location.SourceSpan, getInnermostNodeForTie: true)
			.FirstAncestorOrSelf<MemberDeclarationSyntax>();
		if (declaration is null)
			return None;

		SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken);
		if (semanticModel is null)
			return None;

		// A field/event-field declaration has no symbol of its own; its variables do.
		ISymbol? symbol = declaration is BaseFieldDeclarationSyntax field
			? semanticModel.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken)
			: semanticModel.GetDeclaredSymbol(declaration, cancellationToken);

		if (symbol is null)
			return None;

		var segments = new List<EnclosingSegment> { new(SymbolKindText.Of(symbol), symbol.Name) };
		for (INamedTypeSymbol? containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			segments.Insert(0, new EnclosingSegment(SymbolKindText.Of(containingType), containingType.Name));

		return new EnclosingPath(NamespaceOf(symbol), segments);
	}

	private static string NamespaceOf(ISymbol symbol)
	{
		INamespaceSymbol? containing = symbol.ContainingNamespace;
		return containing is null || containing.IsGlobalNamespace ? "<global>" : containing.ToDisplayString();
	}
}
