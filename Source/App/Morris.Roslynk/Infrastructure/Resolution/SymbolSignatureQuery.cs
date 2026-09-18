namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// A caller-supplied symbol name broken into the parts resolution needs: the head (the name with any
/// trailing parameter list removed), the last segment of that head (what a declaration search is keyed on),
/// the generic arity written on that segment, and the parameter list if one was written.
/// <see cref="ListKind"/> of <see cref="ParameterListKind.None"/> means the caller wrote no list, so the
/// query is parameter-agnostic and matches every overload — the behaviour a bare name has always had.
/// </summary>
public sealed class SymbolSignatureQuery
{
	/// <summary>The name with any trailing parameter list stripped, e.g. <c>N.T.M</c> or <c>N.T.M&lt;T&gt;</c>.</summary>
	public string QualifiedName { get; }

	/// <summary>The last dot-separated segment of <see cref="QualifiedName"/>, with any generic argument list removed.</summary>
	public string SimpleName { get; }

	/// <summary>The generic arity written on the last segment, or -1 when none was written.</summary>
	public int Arity { get; }

	/// <summary>Whether <see cref="QualifiedName"/> carries a namespace or type prefix, i.e. a dot outside brackets.</summary>
	public bool Qualified { get; }

	public ParameterListKind ListKind { get; }

	public IReadOnlyList<SymbolSignatureParameter> Parameters { get; }

	public SymbolSignatureQuery(
		string qualifiedName,
		string simpleName,
		int arity,
		bool qualified,
		ParameterListKind listKind,
		IReadOnlyList<SymbolSignatureParameter> parameters)
	{
		QualifiedName = qualifiedName ?? throw new ArgumentNullException(nameof(qualifiedName));
		SimpleName = simpleName ?? throw new ArgumentNullException(nameof(simpleName));
		Arity = arity;
		Qualified = qualified;
		ListKind = listKind;
		Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
	}
}
