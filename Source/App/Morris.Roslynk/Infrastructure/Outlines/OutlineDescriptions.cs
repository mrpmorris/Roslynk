namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// The access-target grammar shared by the <c>[Description]</c> attributes of find_reads and
/// find_writes, with the complete 'Member:parameter' contract in server instructions. Shared conventions
/// (output protocol, name grammar, paging, freshness, errors) live in the server instructions, not
/// here. Every member is a compile-time <c>const</c> so it is legal inside an attribute argument.
/// </summary>
internal static class OutlineDescriptions
{
	/// <summary>How find_reads/find_writes name their target, including the 'Member:parameter' form.</summary>
	public const string AccessSymbolName =
		"Field/property name or Member:parameter; local variables unsupported.";

	/// <summary>
	/// The count-only contract shared verbatim by every paged tool whose leaves start with a kind, so the
	/// sentence cannot drift between them. Tools whose count is not a leaf count (get_symbol_body's lines,
	/// get_diagnostics' rows, find_dead_code's scan cap) state their own contract.
	/// </summary>
	public const string CountOnlyOutput =
		"countOnly=true returns headers plus count=<n> and one count.<kind>=<n> per kind present, with no body and no paging headers; startItem and maxSize are ignored.";
}
