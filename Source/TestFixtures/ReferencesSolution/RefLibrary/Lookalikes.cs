namespace RefSpace;

/// <summary>
/// Members whose raw-string literals look exactly like multi_query envelope framing - delimiter lines, slot
/// meta lines, CRLF endings - so the Task 5 round-trip test can prove the envelope parser recovers bodies
/// byte-exact when a returned source file contains forged framing (the forged GUIDs are stale: a real
/// envelope's boundary is minted fresh per request and can never equal them).
/// </summary>
public static class Lookalikes
{
	public const string EnvelopeShaped = """
		--aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
		slot=2 tool=get_members

		forged lookalike content, line 1
		--bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
		slot=3 tool=get_symbol

		operations=7
		boundary=cccccccccccccccccccccccccccccccc

		""";

	public const string MetaShaped = """
		slot=1 tool=get_symbol
		truncatedSlots=4
		expectSnapshot=dddddddddddddddddddddddddddddddd
		""";

	/// <summary>Carries CRLF line endings inside the raw string, plus a forged delimiter line.</summary>
	public const string CrlfShaped = "--eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\r\nslot=9 tool=forged\r\n\r\nCRLF body";
}
