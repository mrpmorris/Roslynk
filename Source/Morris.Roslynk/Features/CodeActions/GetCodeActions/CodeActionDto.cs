namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

/// <summary>
/// One available code action: a stable <c>ActionId</c> to pass to apply_code_action, its title, whether
/// it is a <c>Fix</c> or <c>Refactoring</c>, and (for fixes) the diagnostic id it addresses.
/// </summary>
public sealed class CodeActionDto
{
	public string ActionId { get; }
	public string Title { get; }
	public string Kind { get; }
	public string? DiagnosticId { get; }

	public CodeActionDto(string actionId, string title, string kind, string? diagnosticId)
	{
		ActionId = actionId;
		Title = title;
		Kind = kind;
		DiagnosticId = diagnosticId;
	}
}
