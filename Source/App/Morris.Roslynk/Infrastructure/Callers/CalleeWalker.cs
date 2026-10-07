using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// The members invoked by one symbol's executable code, read from the compiler's operation model — the
/// bound form the compiler itself lowers — rather than from syntax: each invocation, object creation,
/// property/event access, method-group reference and user-defined operator or conversion binds to its exact
/// overload, reported as declared (a generic instantiation as its definition, a reduced extension call as
/// its static declaration). Only calls the source spells out are reported, plus the <c>Dispose</c> a <c>using</c> runs:
/// the plumbing the compiler inserts for <c>foreach</c> (<c>GetEnumerator</c>, <c>MoveNext</c>) and
/// <c>await</c> (<c>GetAwaiter</c>, <c>GetResult</c>) is left out as noise, while an explicit
/// <c>.GetAwaiter()</c> call is an ordinary invocation. Shared by get_callees (the inverse view of get_callers).
/// </summary>
/// <remarks>
/// A member's code is more than a method body: a property or indexer is its accessor bodies or expression
/// body, a field is its initializer, and a constructor also runs the initializers of the fields and
/// properties it is responsible for (none if it chains to <c>this(...)</c>). The walked roots are every part
/// of a partial declaration, so lambdas and local functions declared inside the member are part of it and
/// their calls count. A declaration with no code (an abstract or interface member, an auto-property, a type)
/// contributes nothing, as does a body outside the solution: there is nothing to report. Not reported:
/// calls hidden in deconstruction assignments and interpolated-string handlers, and (see above) the implicit
/// <c>foreach</c>/<c>await</c> plumbing.
/// </remarks>
public static class CalleeWalker
{
	public static async Task<IReadOnlyList<ISymbol>> CalleesOfAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
	{
		if (symbol is null)
			throw new ArgumentNullException(nameof(symbol));
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var collector = new CalleeCollector();
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

		return collector.Callees.ToArray();
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

	private sealed class CalleeCollector : OperationWalker
	{
		public HashSet<ISymbol> Callees { get; } = new(SymbolEqualityComparer.Default);

		/// <summary>
		/// Records the callee as declared: a generic instantiation (List&lt;int&gt;.Add, To&lt;string&gt;) maps to
		/// its definition so instantiations collapse to one entry, and an extension method called in reduced
		/// form (xs.Select(f)) maps to its static declaration, the 'this' parameter included.
		/// </summary>
		private void Add(ISymbol symbol)
		{
			if (symbol is IMethodSymbol { ReducedFrom: { } unreduced })
				symbol = unreduced;

			Callees.Add(symbol.OriginalDefinition);
		}

		public override void VisitInvocation(IInvocationOperation operation)
		{
			Add(operation.TargetMethod);
			base.VisitInvocation(operation);
		}

		public override void VisitObjectCreation(IObjectCreationOperation operation)
		{
			if (operation.Constructor is not null)
				Add(operation.Constructor);

			base.VisitObjectCreation(operation);
		}

		public override void VisitMethodReference(IMethodReferenceOperation operation)
		{
			// A method group converted to a delegate (xs.Select(Helper)): the method is not called here but
			// is what the delegate will run.
			Add(operation.Method);
			base.VisitMethodReference(operation);
		}

		public override void VisitPropertyReference(IPropertyReferenceOperation operation)
		{
			// The accessor that actually runs: an assignment target runs the setter (a compound assignment
			// or increment reads too), any other appearance reads the getter. A property without the
			// accessor just used falls back to the property itself.
			IPropertySymbol property = operation.Property;
			if (operation.Parent is IAssignmentOperation assignment
				&& ReferenceEquals(assignment.Target, operation))
			{
				Add(property.SetMethod ?? (ISymbol)property);
				if (assignment is ICompoundAssignmentOperation)
					Add(property.GetMethod ?? (ISymbol)property);
			}
			else if (operation.Parent is IIncrementOrDecrementOperation increment
				&& ReferenceEquals(increment.Target, operation))
			{
				Add(property.GetMethod ?? (ISymbol)property);
				Add(property.SetMethod ?? (ISymbol)property);
			}
			else
			{
				Add(property.GetMethod ?? (ISymbol)property);
			}

			base.VisitPropertyReference(operation);
		}

		public override void VisitEventAssignment(IEventAssignmentOperation operation)
		{
			// 'e += h' / 'e -= h' call the add / remove accessor. A plain read of a field-like event inside
			// its own class is a field access, not a call, so event references are not reported.
			if (operation.EventReference is IEventReferenceOperation { Event: { } @event })
				Add((operation.Adds ? @event.AddMethod : @event.RemoveMethod) ?? (ISymbol)@event);

			base.VisitEventAssignment(operation);
		}

		public override void VisitBinaryOperator(IBinaryOperation operation)
		{
			if (operation.OperatorMethod is not null)
				Add(operation.OperatorMethod);

			base.VisitBinaryOperator(operation);
		}

		public override void VisitUnaryOperator(IUnaryOperation operation)
		{
			if (operation.OperatorMethod is not null)
				Add(operation.OperatorMethod);

			base.VisitUnaryOperator(operation);
		}

		public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
		{
			if (operation.OperatorMethod is not null)
				Add(operation.OperatorMethod);

			base.VisitCompoundAssignment(operation);
		}

		public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
		{
			if (operation.OperatorMethod is not null)
				Add(operation.OperatorMethod);

			base.VisitIncrementOrDecrement(operation);
		}

		public override void VisitConversion(IConversionOperation operation)
		{
			if (operation.OperatorMethod is not null)
				Add(operation.OperatorMethod);

			base.VisitConversion(operation);
		}

		public override void VisitUsing(IUsingOperation operation)
		{
			AddDisposeOf(operation.Resources, operation.IsAsynchronous, operation.SemanticModel);
			base.VisitUsing(operation);
		}

		public override void VisitUsingDeclaration(IUsingDeclarationOperation operation)
		{
			AddDisposeOf(operation.DeclarationGroup, operation.IsAsynchronous, operation.SemanticModel);
			base.VisitUsingDeclaration(operation);
		}

		/// <summary>
		/// The public API does not expose the dispose method a <c>using</c> binds to, so it is found the way the
		/// compiler does: the interface implementation, else a pattern-based accessible <c>Dispose()</c>
		/// (ref structs).
		/// </summary>
		private void AddDisposeOf(IOperation resources, bool isAsynchronous, SemanticModel? semanticModel)
		{
			if (semanticModel is null)
				return;

			string name = isAsynchronous ? "DisposeAsync" : "Dispose";
			INamedTypeSymbol? disposable = semanticModel.Compilation.GetTypeByMetadataName(isAsynchronous ? "System.IAsyncDisposable" : "System.IDisposable");
			IMethodSymbol? interfaceMethod = disposable?.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault();

			IEnumerable<ITypeSymbol?> types = resources is IVariableDeclarationGroupOperation group
				? group.Declarations.SelectMany(declaration => declaration.Declarators).Select(declarator => (ITypeSymbol?)declarator.Symbol.Type)
				: new[] { resources.Type };

			foreach (ITypeSymbol? type in types)
			{
				if (type is null)
					continue;

				if (interfaceMethod is not null && type.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol implementation)
				{
					Add(implementation);
					continue;
				}

				IMethodSymbol? pattern = null;
				for (ITypeSymbol? current = type; current is not null && pattern is null; current = current.BaseType)
				{
					pattern = current.GetMembers(name)
						.OfType<IMethodSymbol>()
						.FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 0);
				}

				AddIfNotNull(pattern ?? (type.TypeKind == TypeKind.Interface ? interfaceMethod : null));
			}
		}

		private void AddIfNotNull(ISymbol? symbol)
		{
			if (symbol is not null)
				Add(symbol);
		}
	}
}
