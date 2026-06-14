using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.CodeActions.ApplyCodeAction;

/// <summary>
/// The outcome of applying a code action or fix (shared by both apply tools). On success
/// <see cref="Applied"/> is true — or false for a checkOnly preview — and <see cref="ChangedFiles"/> lists
/// what was rewritten; <see cref="Action"/> echoes which action ran. A refusal is carried on
/// <see cref="ResultBase.Error"/>: Invalid for a bad actionId, NotFound when the document or diagnostic is
/// missing, NotSupported when no fix exists, Conflict when the action no longer resolves against the code.
/// </summary>
public sealed record ApplyCodeActionResult : ResultBase
{
	public bool Applied { get; init; }
	public IReadOnlyList<string>? ChangedFiles { get; init; }
	public string? Action { get; init; }
}
