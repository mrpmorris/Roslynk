using Morris.Roslynk.Infrastructure.Documentation;

namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// One method's full detail: signature, return type, accessibility and modifiers, ordered parameters and
/// type parameters, source location (1-based, null for metadata), and normalized documentation. Overloads
/// are returned as separate entries since they share a fully-qualified name.
/// </summary>
public sealed record MethodDto(
	string Name,
	string FullName,
	string Signature,
	string ReturnType,
	string Accessibility,
	IReadOnlyList<string> Modifiers,
	IReadOnlyList<ParameterDto> Parameters,
	IReadOnlyList<string> TypeParameters,
	string? SourcePath,
	int? StartLine,
	int? StartColumn,
	int? EndLine,
	int? EndColumn,
	SymbolDocumentation Documentation);
