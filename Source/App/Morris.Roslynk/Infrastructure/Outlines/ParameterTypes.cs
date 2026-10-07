using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// A method's parameter types as one outline field: each type minimally qualified, without ref kinds, and
/// pipe-delimited, with a type that itself contains a comma (e.g. Func&lt;T, TResult&gt;) single-quoted by
/// <see cref="OutlineBuilder.Field"/>. Shared by get_members and get_callees so both render a parameter list
/// identically.
/// </summary>
public static class ParameterTypes
{
	public static string Of(IMethodSymbol method) =>
		string.Join(
			OutlineBuilder.LocationSeparator,
			method.Parameters.Select(parameter => OutlineBuilder.Field(parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))));
}
