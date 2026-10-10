using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// The members invoked by one symbol's executable code, read from the compiler's operation model — the
/// bound form the compiler itself lowers — rather than from syntax: each invocation, object creation,
/// property/event access, method-group reference and user-defined operator or conversion binds to its exact
/// overload, reported as declared (a generic instantiation as its definition, a reduced extension call as
/// its static declaration). Only <see cref="CallKind.Ordinary"/> calls are reported, plus the
/// <c>Dispose</c> a <c>using</c> runs: the plumbing the compiler inserts for <c>foreach</c>
/// (<c>GetEnumerator</c>, <c>MoveNext</c>) and <c>await</c> (<c>GetAwaiter</c>, <c>GetResult</c>) is left
/// out as noise, while an explicit <c>.GetAwaiter()</c> call is an ordinary invocation. Shared by
/// get_callees (the inverse view of get_callers) through <see cref="CallCollector"/>.
/// </summary>
/// <remarks>
/// A member's code is more than a method body: a property or indexer is its accessor bodies or expression
/// body, a field is its initializer, and a constructor also runs the initializers of the fields and
/// properties it is responsible for (none if it chains to <c>this(...)</c>). The walked roots are every part
/// of a partial declaration, so lambdas and local functions declared inside the member are part of it and
/// their calls count. A declaration with no code (an abstract or interface member, an auto-property, a type)
/// contributes nothing, as does a body outside the solution: there is nothing to report.
/// </remarks>
public static class CalleeWalker
{
	public static async Task<IReadOnlyList<ISymbol>> CalleesOfAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
	{
		if (symbol is null)
			throw new ArgumentNullException(nameof(symbol));
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var callees = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		var collector = new CallCollector((callee, kind) =>
		{
			if (kind == CallKind.Ordinary)
				callees.Add(callee);
		});

		foreach (SyntaxReference declaration in symbol.DeclaringSyntaxReferences)
		{
			foreach (SyntaxNode root in ExecutableRoots(symbol, declaration.GetSyntax(cancellationToken), cancellationToken))
			{
				Document? document = solution.GetDocument(root.SyntaxTree);
				if (document is null)
					continue;

				SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken);
				IOperation? operation = semanticModel?.GetOperation(root, cancellationToken);
				if (operation is not null)
					collector.Visit(operation);
			}
		}

		return callees.ToArray();
	}

	/// <summary>The syntax nodes holding the code that runs when <paramref name="symbol"/> is exercised.</summary>
	private static IEnumerable<SyntaxNode> ExecutableRoots(ISymbol symbol, SyntaxNode declaration, CancellationToken cancellationToken)
	{
		switch (declaration)
		{
			case PropertyDeclarationSyntax property:
				if (property.ExpressionBody is not null)
					yield return property.ExpressionBody;
				foreach (SyntaxNode accessor in Accessors(property))
					yield return accessor;
				if (property.Initializer is not null)
					yield return property.Initializer;
				break;

			case IndexerDeclarationSyntax indexer:
				if (indexer.ExpressionBody is not null)
					yield return indexer.ExpressionBody;
				foreach (SyntaxNode accessor in Accessors(indexer))
					yield return accessor;
				break;

			case EventDeclarationSyntax eventDeclaration:
				foreach (SyntaxNode accessor in Accessors(eventDeclaration))
					yield return accessor;
				break;

			case VariableDeclaratorSyntax { Initializer: { } initializer }:
				yield return initializer;
				break;

			case VariableDeclaratorSyntax:
				break;

			case ConstructorDeclarationSyntax constructor:
				yield return constructor;
				if (constructor.Initializer?.IsKind(SyntaxKind.ThisConstructorInitializer) != true)
				{
					foreach (SyntaxNode initializer in FieldAndPropertyInitializers(symbol, cancellationToken))
						yield return initializer;
				}
				break;

			default:
				yield return declaration;
				break;
		}
	}

	private static IEnumerable<SyntaxNode> Accessors(BasePropertyDeclarationSyntax declaration)
	{
		foreach (AccessorDeclarationSyntax accessor in declaration.AccessorList?.Accessors ?? default)
		{
			if (accessor.Body is not null || accessor.ExpressionBody is not null)
				yield return accessor;
		}
	}

	/// <summary>
	/// The initializers the constructor runs before its body: instance ones for an instance constructor,
	/// static ones for a static constructor, across every part of a partial type.
	/// </summary>
	private static IEnumerable<SyntaxNode> FieldAndPropertyInitializers(ISymbol constructor, CancellationToken cancellationToken)
	{
		if (constructor.ContainingType is not INamedTypeSymbol type)
			yield break;

		foreach (SyntaxReference part in type.DeclaringSyntaxReferences)
		{
			if (part.GetSyntax(cancellationToken) is not TypeDeclarationSyntax typeDeclaration)
				continue;

			foreach (MemberDeclarationSyntax member in typeDeclaration.Members)
			{
				switch (member)
				{
					case FieldDeclarationSyntax field when IsStatic(field.Modifiers) == constructor.IsStatic:
						foreach (VariableDeclaratorSyntax declarator in field.Declaration.Variables)
						{
							if (declarator.Initializer is not null)
								yield return declarator.Initializer;
						}

						break;

					case PropertyDeclarationSyntax { Initializer: { } initializer } property when IsStatic(property.Modifiers) == constructor.IsStatic:
						yield return initializer;
						break;
				}
			}
		}
	}

	private static bool IsStatic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.StaticKeyword);
}
