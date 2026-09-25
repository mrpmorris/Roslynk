using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Local functions are named as members of the member that declares them: <c>N.T.Method.local</c>, and
/// <c>N.T.Method.local.inner</c> for one declared inside another. Roslyn gives a local function no qualified
/// name (its display name is the bare <c>local</c>) and its declaration index does not contain local functions,
/// so this is where their container chain is worked out and where they are found by name.
/// </summary>
public static class LocalFunctions
{
	public static bool IsLocalFunction(ISymbol symbol) =>
		symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction };

	/// <summary>
	/// The named symbol a local function is declared in: the method, constructor, operator or outer local
	/// function around it, or the property/event whose accessor it is in. Lambdas and anonymous methods have
	/// no name, so a local function inside one belongs to the named member around the lambda.
	/// </summary>
	public static ISymbol NamedContainer(IMethodSymbol localFunction)
	{
		ISymbol container = localFunction.ContainingSymbol;
		while (container is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LambdaMethod } anonymous)
			container = anonymous.ContainingSymbol;

		return container is IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor
			? accessor.AssociatedSymbol!
			: container;
	}

	/// <summary>
	/// The local functions declared directly in <paramref name="container"/> (not inside another local function
	/// of it) whose name is <paramref name="name"/>.
	/// </summary>
	public static Task<IReadOnlyList<IMethodSymbol>> FindInAsync(Solution solution, ISymbol container, string name, CancellationToken cancellationToken = default) =>
		FindInAsync(solution, container, candidate => string.Equals(candidate, name, StringComparison.Ordinal), cancellationToken);

	/// <summary>Every local function declared directly in <paramref name="container"/>, whatever its name.</summary>
	public static Task<IReadOnlyList<IMethodSymbol>> FindAllInAsync(Solution solution, ISymbol container, CancellationToken cancellationToken = default) =>
		FindInAsync(solution, container, _ => true, cancellationToken);

	private static async Task<IReadOnlyList<IMethodSymbol>> FindInAsync(Solution solution, ISymbol container, Func<string, bool> name, CancellationToken cancellationToken)
	{
		var found = new List<IMethodSymbol>();
		var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

		foreach (SyntaxReference reference in DeclaringReferences(container))
		{
			SyntaxNode declaration = await reference.GetSyntaxAsync(cancellationToken);
			Document? document = solution.GetDocument(declaration.SyntaxTree);
			if (document is null || await document.GetSemanticModelAsync(cancellationToken) is not SemanticModel model)
				continue;

			foreach (LocalFunctionStatementSyntax statement in declaration.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
			{
				if (!name(statement.Identifier.ValueText))
					continue;

				if (model.GetDeclaredSymbol(statement, cancellationToken) is IMethodSymbol local
					&& SymbolEqualityComparer.Default.Equals(NamedContainer(local), container)
					&& seen.Add(local))
				{
					found.Add(local);
				}
			}
		}

		return found;
	}

	/// <summary>Every local function in the solution whose name satisfies <paramref name="predicate"/>.</summary>
	public static async Task<IReadOnlyList<IMethodSymbol>> FindAllAsync(Solution solution, Func<string, bool> predicate, CancellationToken cancellationToken = default)
	{
		var found = new List<IMethodSymbol>();
		foreach (Project project in solution.Projects)
		{
			foreach (Document document in project.Documents)
			{
				if (await document.GetSyntaxRootAsync(cancellationToken) is not SyntaxNode root)
					continue;

				List<LocalFunctionStatementSyntax> statements = root.DescendantNodes()
					.OfType<LocalFunctionStatementSyntax>()
					.Where(statement => predicate(statement.Identifier.ValueText))
					.ToList();
				if (statements.Count == 0 || await document.GetSemanticModelAsync(cancellationToken) is not SemanticModel model)
					continue;

				foreach (LocalFunctionStatementSyntax statement in statements)
				{
					if (model.GetDeclaredSymbol(statement, cancellationToken) is IMethodSymbol local)
						found.Add(local);
				}
			}
		}

		return found;
	}

	/// <summary>
	/// The named containers of <paramref name="symbol"/> from the outermost member down to the nearest, for a
	/// local function; empty for anything else. <c>N.T.M.a.b</c> gives <c>[M, a]</c> for <c>b</c>.
	/// </summary>
	public static IReadOnlyList<ISymbol> ContainerChain(ISymbol symbol)
	{
		var chain = new List<ISymbol>();
		ISymbol current = symbol;
		while (current is IMethodSymbol local && IsLocalFunction(local))
		{
			current = NamedContainer(local);
			chain.Insert(0, current);
		}

		return chain;
	}

	private static IEnumerable<SyntaxReference> DeclaringReferences(ISymbol container) =>
		container switch
		{
			IPropertySymbol property => property.DeclaringSyntaxReferences,
			IEventSymbol @event => @event.DeclaringSyntaxReferences,
			_ => container.DeclaringSyntaxReferences
		};
}
