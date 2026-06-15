using Morris.Roslynk.Infrastructure.Documentation;

namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// One method's full detail: signature, return type, accessibility and modifiers, ordered parameters and
/// type parameters, source location (1-based, null for metadata), the source body when requested, and
/// normalized documentation. Overloads are returned as separate entries since they share a fully-qualified
/// name.
/// </summary>
public sealed class MethodDto
{
	public string Name { get; }
	public string FullName { get; }
	public string Signature { get; }
	public string ReturnType { get; }
	public string Accessibility { get; }
	public IReadOnlyList<string> Modifiers { get; }
	public IReadOnlyList<ParameterDto> Parameters { get; }
	public IReadOnlyList<string> TypeParameters { get; }
	public string? SourcePath { get; }
	public int? StartLine { get; }
	public int? StartColumn { get; }
	public int? EndLine { get; }
	public int? EndColumn { get; }
	public string? Body { get; }
	public SymbolDocumentation Documentation { get; }

	public MethodDto(
		string name,
		string fullName,
		string signature,
		string returnType,
		string accessibility,
		IReadOnlyList<string> modifiers,
		IReadOnlyList<ParameterDto> parameters,
		IReadOnlyList<string> typeParameters,
		string? sourcePath,
		int? startLine,
		int? startColumn,
		int? endLine,
		int? endColumn,
		string? body,
		SymbolDocumentation documentation)
	{
		Name = name;
		FullName = fullName;
		Signature = signature;
		ReturnType = returnType;
		Accessibility = accessibility;
		Modifiers = modifiers;
		Parameters = parameters;
		TypeParameters = typeParameters;
		SourcePath = sourcePath;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
		Body = body;
		Documentation = documentation;
	}
}
