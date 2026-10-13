namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// The access-target grammar shared by the <c>[Description]</c> attributes of find_reads and
/// find_writes, so the 'Member:parameter' form is defined once. Conventions shared across all tools
/// (output protocol, name grammar, paging, freshness, errors) live in the server instructions, not
/// here. Every member is a compile-time <c>const</c> so it is legal inside an attribute argument.
/// </summary>
internal static class OutlineDescriptions
{
	/// <summary>How find_reads/find_writes name their target, including the 'Member:parameter' form.</summary>
	public const string AccessSymbolName =
		"Fully-qualified name of a field or property, e.g. 'MyNamespace.MyType.MyField'; for a parameter, the "
		+ "fully-qualified name of its containing method, constructor, indexer or local function, then ':' and the "
		+ "parameter name, e.g. 'MyNamespace.MyType.MyMethod:value' or 'MyNamespace.MyType.MyMethod(int):value'. "
		+ "Local variables are not supported (error=NotSupported).";
}
