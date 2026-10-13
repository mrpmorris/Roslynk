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
}
