namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

/// <summary>The code actions available at a position, with a <c>Message</c> when none apply or the document is unknown.</summary>
public sealed record GetCodeActionsResponse(IReadOnlyList<CodeActionDto> Actions, string? Message);
