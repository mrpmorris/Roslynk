namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// How much detail a rendered signature carries. A tool emits the lowest tier that tells its candidates
/// apart; the resolver accepts every tier, so any emitted candidate resolves when sent back. Ref modifiers
/// come before namespaces because a ref/value overload pair is the cheaper thing to spell out.
/// </summary>
public enum SignatureTier
{
	/// <summary>Minimally-qualified parameter types with keyword aliases, e.g. <c>(int, string)</c>.</summary>
	Minimal,

	/// <summary><see cref="Minimal"/> plus the ref modifier, e.g. <c>(ref int)</c>.</summary>
	MinimalWithRefKinds,

	/// <summary>Namespace-qualified parameter types, e.g. <c>(System.Int32, System.String)</c>.</summary>
	FullyQualified,

	/// <summary><see cref="FullyQualified"/> plus the ref modifier, e.g. <c>(ref System.Int32)</c>.</summary>
	FullyQualifiedWithRefKinds
}
