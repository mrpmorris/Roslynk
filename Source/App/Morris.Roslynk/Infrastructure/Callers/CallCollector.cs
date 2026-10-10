using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>How a call appears in the source.</summary>
internal enum CallKind
{
	/// <summary>
	/// Spelled out, or an implicit invocation get_callees has always listed: invocations (implicit ones
	/// included — query methods, collection initializers, interpolated-string handler members, implicit
	/// <c>base()</c>), object creation, property/event accessors, method groups, operators, conversions, a
	/// using's <c>Dispose</c>.
	/// </summary>
	Ordinary,

	/// <summary>
	/// Plumbing the compiler inserts for <c>foreach</c> (incl. <c>await foreach</c>), <c>await</c>,
	/// deconstruction, positional patterns, implicit <c>^</c>/<c>..</c> support and collection expressions.
	/// </summary>
	Plumbing
}

/// <summary>
/// The members one operation tree calls, as the compiler binds them. Shared by get_callees (forward, over a
/// member's roots) and get_callers' implicit-call scan (reverse, over candidate sites), so both tools define
/// a call identically and cannot drift. Callees are reported as declared: a reduced extension call as its
/// static declaration, a generic instantiation as its definition.
/// </summary>
internal sealed class CallCollector : OperationWalker
{
	private readonly Action<ISymbol, CallKind> Report;

	public CallCollector(Action<ISymbol, CallKind> report) =>
		Report = report ?? throw new ArgumentNullException(nameof(report));

	/// <summary>A generic instantiation as its definition, a reduced extension as its static declaration.</summary>
	public static ISymbol Normalize(ISymbol symbol)
	{
		if (symbol is IMethodSymbol { ReducedFrom: { } unreduced })
			symbol = unreduced;

		return symbol.OriginalDefinition;
	}

	private void Add(ISymbol? symbol, CallKind kind = CallKind.Ordinary)
	{
		if (symbol is not null)
			Report(Normalize(symbol), kind);
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

	public override void VisitForEachLoop(IForEachLoopOperation operation)
	{
		if (operation.Syntax is CommonForEachStatementSyntax syntax && operation.SemanticModel is SemanticModel model)
		{
			ForEachStatementInfo info = model.GetForEachStatementInfo(syntax);
			Add(info.GetEnumeratorMethod, CallKind.Plumbing);
			Add(info.MoveNextMethod, CallKind.Plumbing);
			Add(info.CurrentProperty?.GetMethod, CallKind.Plumbing);
			Add(DisposeOf(info), CallKind.Plumbing);
			AddAwaiter(info.MoveNextAwaitableInfo);
			AddAwaiter(info.DisposeAwaitableInfo);
			Add(info.ElementConversion.MethodSymbol, CallKind.Plumbing);
			Add(info.CurrentConversion.MethodSymbol, CallKind.Plumbing);
			if (syntax is ForEachVariableStatementSyntax variable)
				AddDeconstruction(model.GetDeconstructionInfo(variable));
		}

		base.VisitForEachLoop(operation);
	}

	public override void VisitAwait(IAwaitOperation operation)
	{
		if (operation.Syntax is AwaitExpressionSyntax syntax && operation.SemanticModel is SemanticModel model)
			AddAwaiter(model.GetAwaitExpressionInfo(syntax));

		base.VisitAwait(operation);
	}

	public override void VisitDeconstructionAssignment(IDeconstructionAssignmentOperation operation)
	{
		if (operation.Syntax is AssignmentExpressionSyntax syntax && operation.SemanticModel is SemanticModel model)
			AddDeconstruction(model.GetDeconstructionInfo(syntax));

		base.VisitDeconstructionAssignment(operation);
	}

	public override void VisitRecursivePattern(IRecursivePatternOperation operation)
	{
		Add(operation.DeconstructSymbol, CallKind.Plumbing);
		base.VisitRecursivePattern(operation);
	}

	public override void VisitImplicitIndexerReference(IImplicitIndexerReferenceOperation operation)
	{
		Add(GetterOf(operation.LengthSymbol), CallKind.Plumbing);
		Add(GetterOf(operation.IndexerSymbol), CallKind.Plumbing);
		base.VisitImplicitIndexerReference(operation);
	}

	public override void VisitListPattern(IListPatternOperation operation)
	{
		Add(GetterOf(operation.LengthSymbol), CallKind.Plumbing);
		Add(GetterOf(operation.IndexerSymbol), CallKind.Plumbing);
		base.VisitListPattern(operation);
	}

	public override void VisitSlicePattern(ISlicePatternOperation operation)
	{
		Add(operation.SliceSymbol, CallKind.Plumbing);
		base.VisitSlicePattern(operation);
	}

	public override void VisitCollectionExpression(ICollectionExpressionOperation operation)
	{
		Add(operation.ConstructMethod, CallKind.Plumbing);
		base.VisitCollectionExpression(operation);
	}

	private void AddAwaiter(AwaitExpressionInfo info)
	{
		Add(info.GetAwaiterMethod, CallKind.Plumbing);
		Add(info.IsCompletedProperty?.GetMethod, CallKind.Plumbing);
		Add(info.GetResultMethod, CallKind.Plumbing);
	}

	private void AddDeconstruction(DeconstructionInfo info)
	{
		Add(info.Method, CallKind.Plumbing);
		Add(info.Conversion?.MethodSymbol, CallKind.Plumbing);
		foreach (DeconstructionInfo nested in info.Nested)
			AddDeconstruction(nested);
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

	/// <summary>
	/// <see cref="ForEachStatementInfo.DisposeMethod"/> names <see cref="IDisposable.Dispose"/> (the interface
	/// member) even when the enumerator is a concrete type; the method that runs is its implementation, unless
	/// the enumerator is itself typed as an interface.
	/// </summary>
	private static IMethodSymbol? DisposeOf(ForEachStatementInfo info)
	{
		if (info.DisposeMethod is not IMethodSymbol dispose)
			return null;

		return info.GetEnumeratorMethod?.ReturnType is ITypeSymbol { TypeKind: not TypeKind.Interface } enumerator
			&& enumerator.FindImplementationForInterfaceMember(dispose) is IMethodSymbol implementation
				? implementation
				: dispose;
	}

	private static ISymbol? GetterOf(ISymbol? symbol) =>
		symbol is IPropertySymbol property ? property.GetMethod ?? (ISymbol)property : symbol;

	private void AddIfNotNull(ISymbol? symbol)
	{
		if (symbol is not null)
			Add(symbol);
	}
}
