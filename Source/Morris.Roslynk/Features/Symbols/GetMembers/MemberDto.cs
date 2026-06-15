namespace Morris.Roslynk.Features.Symbols.GetMembers;

/// <summary>
/// A member of a type: its name, kind, accessibility, a readable signature, and its source location
/// (1-based, null for members that come from metadata). Read the member's source by opening SourcePath and
/// reading StartLine through EndLine with the file tool.
/// </summary>
public sealed class MemberDto
{
	public string Name { get; }
	public string Kind { get; }
	public string Accessibility { get; }
	public string Signature { get; }
	public string? SourcePath { get; }
	public int? StartLine { get; }
	public int? EndLine { get; }

	public MemberDto(
		string name,
		string kind,
		string accessibility,
		string signature,
		string? sourcePath,
		int? startLine,
		int? endLine)
	{
		Name = name;
		Kind = kind;
		Accessibility = accessibility;
		Signature = signature;
		SourcePath = sourcePath;
		StartLine = startLine;
		EndLine = endLine;
	}
}
