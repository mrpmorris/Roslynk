using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.References;

/// <summary>
/// Roslyn 5.9's CSharpSemanticFactsService.TryGetDisposeMethod matches every single-variable local
/// declaration with an initializer, with or without 'using', so a plain <c>Resource r = new Resource();</c>
/// is reported as a reference to (or caller of) Dispose/DisposeAsync at the declaration's type token.
/// A real using is reported at its 'using' keyword and an explicit call at the method name, so a location
/// inside the type span of a declaration with no 'using' keyword is never real. 'await using' declarations
/// keep their 'using' keyword and are never spurious. The filter is a no-op once fixed upstream.
/// </summary>
internal static class SpuriousDisposeReferences
{
	public static bool IsDisposeMethod(ISymbol? symbol) =>
		symbol is IMethodSymbol { Name: WellKnownMemberNames.DisposeMethodName or WellKnownMemberNames.DisposeAsyncMethodName };

	/// <summary>Whether <paramref name="span"/> is the type token of a local declaration with no 'using'.</summary>
	public static bool IsSpurious(SyntaxNode root, TextSpan span)
	{
		SyntaxNode node = root.FindNode(span, getInnermostNodeForTie: true);
		return node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is { } declaration
			&& declaration.UsingKeyword.IsKind(SyntaxKind.None)
			&& declaration.Declaration.Type.Span.Contains(span);
	}

	public static async Task<bool> IsSpuriousAsync(Location location, CancellationToken cancellationToken)
	{
		if (location.SourceTree is not SyntaxTree tree)
			return false;

		SyntaxNode root = await tree.GetRootAsync(cancellationToken);
		return IsSpurious(root, location.SourceSpan);
	}

	/// <summary>
	/// Whether every location is spurious. A caller/reference is dropped only when none of its locations is
	/// real; an empty set is vacuously all-spurious, matching the original get_callers filter.
	/// </summary>
	public static async Task<bool> AreAllSpuriousAsync(IEnumerable<Location> locations, CancellationToken cancellationToken)
	{
		foreach (Location location in locations)
		{
			if (!await IsSpuriousAsync(location, cancellationToken))
				return false;
		}

		return true;
	}
}
