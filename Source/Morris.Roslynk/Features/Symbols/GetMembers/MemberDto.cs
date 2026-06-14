namespace Morris.Roslynk.Features.Symbols.GetMembers;

/// <summary>A member of a type: its name, kind, accessibility, and a readable signature.</summary>
public sealed class MemberDto
{
	public string Name { get; }
	public string Kind { get; }
	public string Accessibility { get; }
	public string Signature { get; }

	public MemberDto(string name, string kind, string accessibility, string signature)
	{
		Name = name;
		Kind = kind;
		Accessibility = accessibility;
		Signature = signature;
	}
}
