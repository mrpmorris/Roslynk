using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Morris.Roslynk.Infrastructure.Accesses;

/// <summary>
/// Classifies how the name at a reference location touches its symbol, purely from the syntax around it
/// (plus the semantic model to recognise a constructor of the symbol's own type). Returns null for a
/// reference that neither reads nor writes the value: nameof(...), a doc-comment cref, or a named argument
/// that merely names a parameter.
/// </summary>
public static class AccessClassifier
{
	public static AccessKind? Classify(SyntaxNode node, ISymbol symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
	{
		if (node is not SimpleNameSyntax name)
		{
			name = node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault()!;
			if (name is null)
				return AccessKind.Read;
		}

		if (name.IsPartOfStructuredTrivia())
			return null;
		if (name.Parent is NameColonSyntax)
			return null;
		if (IsInNameOf(name, semanticModel, cancellationToken))
			return null;

		// Climb from the bare name to the whole target expression: 'F', 'this.F', 'x.F', 'x?.F', '(F)', 'F!'.
		ExpressionSyntax target = name;
		while (true)
		{
			SyntaxNode? parent = target.Parent;
			if (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == target)
				target = memberAccess;
			else if (parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == target)
				target = memberBinding;
			else if (parent is ParenthesizedExpressionSyntax parenthesized)
				target = parenthesized;
			else if (parent is PostfixUnaryExpressionSyntax suppress && suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
				target = suppress;
			else
				break;
		}

		switch (target.Parent)
		{
			case AssignmentExpressionSyntax assignment when assignment.Left == target:
				if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
					return AccessKind.Compound;
				if (assignment.Parent is InitializerExpressionSyntax initializer
					&& initializer.Kind() is SyntaxKind.ObjectInitializerExpression or SyntaxKind.WithInitializerExpression)
				{
					// 'new C { P = { X = 1 } }' reads P to populate it; 'new C { P = value }' initialises it.
					return assignment.Right is InitializerExpressionSyntax ? AccessKind.Read : AccessKind.Init;
				}
				return IsConstructorAssignment(target, symbol, semanticModel, cancellationToken) ? AccessKind.Init : AccessKind.Assign;

			case PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
				return AccessKind.Increment;

			case PostfixUnaryExpressionSyntax postfix when postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
				return AccessKind.Increment;

			case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
				return AccessKind.Ref;

			case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
				return AccessKind.Out;

			case ArgumentSyntax argument when argument.Parent is TupleExpressionSyntax tuple && IsDeconstructionTarget(tuple):
				return AccessKind.Assign;

			case RefExpressionSyntax:
				return AccessKind.Ref;

			case PrefixUnaryExpressionSyntax addressOf when addressOf.IsKind(SyntaxKind.AddressOfExpression):
				return AccessKind.Ref;

			case NameEqualsSyntax { Parent: AttributeArgumentSyntax }:
				return AccessKind.Init;

			default:
				return AccessKind.Read;
		}
	}

	private static bool IsDeconstructionTarget(TupleExpressionSyntax tuple)
	{
		ExpressionSyntax current = tuple;
		while (current.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax outer })
			current = outer;

		return current.Parent switch
		{
			AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left == current,
			ForEachVariableStatementSyntax forEach => forEach.Variable == current,
			_ => false,
		};
	}

	private static bool IsInNameOf(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
	{
		foreach (InvocationExpressionSyntax invocation in node.Ancestors().OfType<InvocationExpressionSyntax>())
		{
			if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
				&& invocation.ArgumentList.Span.Contains(node.Span)
				&& semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is null)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// An assignment to a field or property of the constructor's own instance (or, from a static constructor,
	/// a static one) — 'F = x' or 'this.F = x' — is initialisation rather than a later mutation.
	/// </summary>
	private static bool IsConstructorAssignment(ExpressionSyntax target, ISymbol symbol, SemanticModel semanticModel, CancellationToken cancellationToken)
	{
		if (symbol is not (IFieldSymbol or IPropertySymbol))
			return false;

		bool ownInstance = target switch
		{
			SimpleNameSyntax => true,
			MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } => true,
			MemberAccessExpressionSyntax memberAccess when symbol.IsStatic => semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol is INamedTypeSymbol,
			_ => false,
		};
		if (!ownInstance)
			return false;

		// Assignments inside a lambda or local function run whenever those are invoked, not during construction.
		SyntaxNode? member = target.Ancestors().FirstOrDefault(ancestor =>
			ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax);
		if (member is not ConstructorDeclarationSyntax constructor)
			return false;

		if (semanticModel.GetDeclaredSymbol(constructor, cancellationToken) is not IMethodSymbol constructorSymbol)
			return false;

		return constructorSymbol.IsStatic == symbol.IsStatic
			&& SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingType.OriginalDefinition, symbol.ContainingType.OriginalDefinition);
	}
}
