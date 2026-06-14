namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

/// <summary>
/// One available code action: a stable <c>ActionId</c> to pass to apply_code_action, its title, whether
/// it is a <c>Fix</c> or <c>Refactoring</c>, and (for fixes) the diagnostic id it addresses.
/// </summary>
public sealed record CodeActionDto(string ActionId, string Title, string Kind, string? DiagnosticId);
