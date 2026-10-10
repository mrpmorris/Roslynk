using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// The number of unresolved (error) types in a symbol's own declared signature. A declaration compiled into
/// several target frameworks or <c>#if</c> projections is one symbol per compilation, and a type that binds in
/// one can fail in another (a <c>using</c> under <c>#if</c>, an API an older framework lacks). An unresolved
/// type renders as written, not as the type it names — <c>Int32</c> rather than <c>int</c>, <c>Type</c> rather
/// than <c>System.Type</c> once qualified — so the copy with the fewest error types is the one whose name a
/// tool should echo. Zero means the signature bound completely.
/// </summary>
/// <remarks>
/// Counts exactly what an echoed name can show: the symbol itself when it is unresolved, a method's return and
/// parameter (including indexer) types, property, event and field types, a type's base type and interfaces
/// (which <c>get_type_hierarchy</c> walks from its representative), a delegate's invoke method, and the
/// parameter list of a local function's container, whose signature is part of the local's rendered name.
/// Type-parameter constraints are deliberately not counted — no renderer emits them — and a resolved member's
/// containing types are not walked: they are declared symbols, and a containing type that failed to bind
/// surfaces as the member's own error types. The walk needs no compilation or semantic model; error types are
/// visible on the symbol graph itself.
/// </remarks>
public static class ErrorTypeCount
{
	/// <summary>How many types of <paramref name="symbol"/>'s declared signature failed to bind.</summary>
	public static int Of(ISymbol symbol)
	{
		ArgumentNullException.ThrowIfNull(symbol);

		int count = symbol switch
		{
			// The symbol itself is unresolved (e.g. found at a source position in a target framework where the
			// identifier does not bind): it renders as written, exactly like one of its arguments would.
			IErrorTypeSymbol error => 1 + TypeArguments(error),
			IMethodSymbol method => Count(method.ReturnType) + Sum(method.Parameters),
			IPropertySymbol property => Count(property.Type) + Sum(property.Parameters),
			IFieldSymbol field => Count(field.Type),
			IEventSymbol @event => Count(@event.Type),
			INamedTypeSymbol type => (type.BaseType is INamedTypeSymbol baseType ? Count(baseType) : 0)
				+ type.Interfaces.Sum(Count)
				+ (type.DelegateInvokeMethod is IMethodSymbol invoke ? Of(invoke) : 0),
			_ => 0
		};

		// A local function's rendered name carries its container's parameter list (N.T.M(int).local(string)),
		// so a container parameter that failed to bind degrades the local's echo exactly like its own.
		if (symbol is IMethodSymbol local && LocalFunctions.IsLocalFunction(local)
			&& LocalFunctions.NamedContainer(local) is ISymbol container)
		{
			count += Of(container);
		}

		return count;
	}

	private static int Sum(ImmutableArray<IParameterSymbol> parameters) =>
		parameters.Sum(parameter => Count(parameter.Type));

	private static int Count(ITypeSymbol type) => type switch
	{
		// An error type is itself a named type, so this arm must come first; its own (often written-name)
		// type arguments are counted too, which also covers Nullable<error> and tuple elements.
		{ TypeKind: TypeKind.Error } => 1 + TypeArguments(type),
		IArrayTypeSymbol array => Count(array.ElementType),
		IPointerTypeSymbol pointer => Count(pointer.PointedAtType),
		IFunctionPointerTypeSymbol functionPointer => Of(functionPointer.Signature),
		INamedTypeSymbol named => TypeArguments(named)
			+ (named.ContainingType is INamedTypeSymbol outer ? Count(outer) : 0),
		_ => 0
	};

	private static int TypeArguments(ITypeSymbol type) =>
		type is INamedTypeSymbol named ? named.TypeArguments.Sum(Count) : 0;
}
