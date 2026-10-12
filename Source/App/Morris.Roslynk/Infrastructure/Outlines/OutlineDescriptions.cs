namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Recurring fragments shared by the <c>[Description]</c> attributes of find_reads and find_writes, so
/// the access-target grammar, the access-kind vocabulary and the projection-coverage rule are defined
/// once. Conventions shared across all tools (output protocol, name grammar, paging, freshness, errors)
/// live in the server instructions, not here. Every member is a compile-time <c>const</c> so it is
/// legal inside an attribute argument.
/// </summary>
internal static class OutlineDescriptions
{
	/// <summary>How find_reads/find_writes name their target, including the 'Member:parameter' form.</summary>
	public const string AccessSymbolName =
		"Fully-qualified name of a field or property, e.g. 'MyNamespace.MyType.MyField'; for a parameter, the "
		+ "fully-qualified name of its containing method, constructor, indexer or local function, then ':' and the "
		+ "parameter name, e.g. 'MyNamespace.MyType.MyMethod:value' or 'MyNamespace.MyType.MyMethod(int):value'. "
		+ "Local variables are not supported (error=NotSupported).";

	/// <summary>The access-kind vocabulary and dual read/write rule shared by find_reads and find_writes.</summary>
	public const string AccessKinds =
		"accessKind is one of read, assign (simple '='), compound ('+=', '??=' and friends), increment ('++'/'--'), "
		+ "ref (passed by ref, 'ref x', or '&x'), out (passed as out), init (a declaration initialiser, an object or "
		+ "'with' initialiser, an attribute named argument, or an assignment to the constructor's own field/property "
		+ "in its constructor). compound, increment and ref both read and write: each such location appears in BOTH "
		+ "find_reads and find_writes with the same accessKind, so do not add the two tools' counts together. "
		+ "nameof(...), doc-comment crefs and named arguments ('M(value: 1)') are neither, and are omitted.";

	/// <summary>The projection limitation stated for find_reads/find_writes.</summary>
	public const string AccessCoverage =
		"Coverage: every loaded target framework, plus derived compilations each toggling ONE preprocessor symbol "
		+ "that is uniformly defined or undefined across the C# projects; a #if branch reachable only with several "
		+ "symbols toggled at once, or guarded by a symbol defined in some projects but not others, is not analysed.";
}
