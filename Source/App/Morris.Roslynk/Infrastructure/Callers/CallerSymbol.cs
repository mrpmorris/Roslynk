using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// The member a call is attributed to: the named method, constructor, operator or local function around it;
/// the property, indexer or event whose accessor it is in; the field (or auto-property) whose initializer it
/// is in. Lambdas and anonymous methods are transparent. This mirrors Roslyn's own
/// GetEnclosingMethodOrPropertyOrField, so callers found by either path are the same symbols.
/// </summary>
internal static class CallerSymbol
{
	/// <summary>
	/// The member the <paramref name="site"/> hangs on, found by walking out to its nearest declaration.
	/// SemanticModel.GetEnclosingSymbol reports the containing type for a position inside a declaration's
	/// header, so the declaring syntax is read directly instead.
	/// </summary>
	public static ISymbol? Of(SemanticModel model, SyntaxNode site, CancellationToken cancellationToken)
	{
		for (SyntaxNode? node = site; node is not null; node = node.Parent)
		{
			ISymbol? declared = node switch
			{
				MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax
					or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
					=> model.GetDeclaredSymbol(node, cancellationToken),

				VariableDeclaratorSyntax declarator when declarator.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax
					=> model.GetDeclaredSymbol(declarator, cancellationToken),

				PropertyDeclarationSyntax { Initializer: not null }
					=> model.GetDeclaredSymbol(node, cancellationToken),

				_ => null
			};

			if (declared is not null)
				return Normalize(declared);
		}

		// A top-level statement is declared by no syntax of its own; its member is the synthesized entry point.
		if (site.AncestorsAndSelf().OfType<GlobalStatementSyntax>().Any())
		{
			ISymbol? enclosing = model.GetEnclosingSymbol(site.SpanStart, cancellationToken);
			return enclosing is null ? null : Normalize(enclosing);
		}

		return null;
	}

	/// <summary>An auto-property initializer binds inside the compiler's backing field; report the property. An accessor reports the property, indexer or event it belongs to.</summary>
	public static ISymbol Normalize(ISymbol caller) =>
		caller switch
		{
			IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise, AssociatedSymbol: { } associated } => associated,
			IFieldSymbol { AssociatedSymbol: IPropertySymbol property } => property,
			_ => caller
		};
}
