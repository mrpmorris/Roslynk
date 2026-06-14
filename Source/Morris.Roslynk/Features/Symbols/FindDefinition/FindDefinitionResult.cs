using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.FindDefinition;

/// <summary>
/// Where the symbol used at the requested position is declared. The location fields are null for symbols
/// with no in-source declaration (metadata); line/column are 1-based. When nothing resolves at the
/// position, <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.NotFound"/>.
/// </summary>
public sealed record FindDefinitionResult : ResultBase
{
	public string? FullName { get; init; }
	public string? Kind { get; init; }
	public string? SourcePath { get; init; }
	public int? StartLine { get; init; }
	public int? StartColumn { get; init; }
	public int? EndLine { get; init; }
	public int? EndColumn { get; init; }
}
