using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.FindDefinition;

/// <summary>
/// Where the symbol used at the requested position is declared. The location fields are null for symbols
/// with no in-source declaration (metadata); line/column are 1-based. When nothing resolves at the
/// position, <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.NotFound"/>.
/// </summary>
public sealed record FindDefinitionResult : ResultBase
{
	public string? FullName { get; }
	public string? Kind { get; }
	public string? SourcePath { get; }
	public int? StartLine { get; }
	public int? StartColumn { get; }
	public int? EndLine { get; }
	public int? EndColumn { get; }

	public FindDefinitionResult(
		SolutionModel solutionModel,
		Error? error,
		string? fullName,
		string? kind,
		string? sourcePath,
		int? startLine,
		int? startColumn,
		int? endLine,
		int? endColumn)
		: base(solutionModel, error)
	{
		FullName = fullName;
		Kind = kind;
		SourcePath = sourcePath;
		StartLine = startLine;
		StartColumn = startColumn;
		EndLine = endLine;
		EndColumn = endColumn;
	}
}
