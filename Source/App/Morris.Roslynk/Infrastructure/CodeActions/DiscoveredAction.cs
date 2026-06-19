using Microsoft.CodeAnalysis.CodeActions;

namespace Morris.Roslynk.Infrastructure.CodeActions;

/// <summary>
/// A code action found at a position: the Roslyn <see cref="CodeAction"/>, whether it is a
/// <c>Fix</c> or a <c>Refactoring</c>, and the diagnostic id it addresses (null for refactorings).
/// </summary>
public sealed class DiscoveredAction
{
	public CodeAction Action { get; }
	public string Kind { get; }
	public string? DiagnosticId { get; }

	public DiscoveredAction(CodeAction action, string kind, string? diagnosticId)
	{
		Action = action;
		Kind = kind;
		DiagnosticId = diagnosticId;
	}
}
