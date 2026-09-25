using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// The lower-cased kind word used on every outline line: a named type reports its TypeKind (class, struct,
/// interface, enum, delegate), anything else its SymbolKind (method, property, field, event, ...). One
/// definition so the vocabulary is identical across find_references, get_members, get_callers and the rest.
/// </summary>
public static class SymbolKindText
{
	public static string Of(ISymbol symbol) =>
		symbol switch
		{
			INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
			IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "localfunction",
			_ => symbol.Kind.ToString().ToLowerInvariant()
		};
}
