namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>Which parameter list, if any, a caller wrote after a symbol name.</summary>
public enum ParameterListKind
{
	/// <summary>No list was written, so the name matches a member of any signature.</summary>
	None,

	/// <summary><c>(...)</c>: a method, constructor or delegate.</summary>
	Parentheses,

	/// <summary><c>[...]</c>: an indexer.</summary>
	Brackets
}
