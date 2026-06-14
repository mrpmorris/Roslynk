namespace Morris.Roslynk.Features.Symbols.GetMembers;

/// <summary>A member of a type: its name, kind, accessibility, and a readable signature.</summary>
public sealed record MemberDto(string Name, string Kind, string Accessibility, string Signature);
