using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.CodeActions;

/// <summary>
/// Discovers code fixes and refactorings at a span and resolves a previously-discovered action back to the
/// solution it would produce. Fixes are driven from the compiler <em>and</em> analyzer diagnostics
/// overlapping the span, so anything <c>get_diagnostics</c> lists is fixable here; refactorings are
/// computed over the span.
/// </summary>
public sealed class CodeActionService
{
	private const int MaxActions = 50;

	private readonly DocumentDiagnosticsProvider DocumentDiagnostics;

	public CodeActionService(DocumentDiagnosticsProvider documentDiagnostics)
	{
		DocumentDiagnostics = documentDiagnostics ?? throw new ArgumentNullException(nameof(documentDiagnostics));
	}

	public async Task<IReadOnlyList<DiscoveredAction>> DiscoverAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		var discovered = new List<DiscoveredAction>();

		// The document's compiler and analyzer diagnostics, narrowed to the span here. The provider's
		// FixableDiagnosticIds gates relevance, so every severity is kept - IDE0005 is hidden by default.
		ImmutableArray<Diagnostic> diagnostics = (await DocumentDiagnostics.GetForDocumentAsync(document, cancellationToken))
			.Where(diagnostic => diagnostic.Location.SourceSpan.IntersectsWith(span))
			.ToImmutableArray();

		foreach (CodeFixProvider provider in CodeActionCatalog.Instance.FixProviders)
		{
			ImmutableArray<string> fixable = CodeActionCatalog.FixableIds(provider);
			foreach (Diagnostic diagnostic in diagnostics)
			{
				if (!fixable.Contains(diagnostic.Id))
					continue;

				var registered = new List<CodeAction>();
				var context = new CodeFixContext(document, diagnostic, (action, _) => registered.Add(action), cancellationToken);
				try
				{
					await provider.RegisterCodeFixesAsync(context);
				}
				catch
				{
					continue;
				}

				foreach (CodeAction action in registered.SelectMany(Leaves))
					discovered.Add(new DiscoveredAction(action, "Fix", CodeActionCatalog.PublicId(diagnostic.Id)));
			}
		}

		foreach (CodeRefactoringProvider provider in CodeActionCatalog.Instance.RefactoringProviders)
		{
			var registered = new List<CodeAction>();
			var context = new CodeRefactoringContext(document, span, action => registered.Add(action), cancellationToken);
			try
			{
				await provider.ComputeRefactoringsAsync(context);
			}
			catch
			{
				continue;
			}

			foreach (CodeAction action in registered.SelectMany(Leaves))
				discovered.Add(new DiscoveredAction(action, "Refactoring", null));
		}

		return discovered.Take(MaxActions).ToArray();
	}

	/// <summary>Re-discovers the action named by <paramref name="actionRef"/> and returns the solution it produces, or null.</summary>
	public async Task<Solution?> ComputeChangedSolutionAsync(Document document, ActionRef actionRef, CancellationToken cancellationToken = default)
	{
		var span = new TextSpan(actionRef.SpanStart, actionRef.SpanLength);
		IReadOnlyList<DiscoveredAction> actions = await DiscoverAsync(document, span, cancellationToken);

		DiscoveredAction? match = actions.FirstOrDefault(action =>
			action.Kind == actionRef.Kind && string.Equals(KeyOf(action.Action), actionRef.Key, StringComparison.Ordinal));
		return match is null ? null : await ChangedSolutionAsync(match.Action, cancellationToken);
	}

	/// <summary>The solution a code action would produce (its first <see cref="ApplyChangesOperation"/>), or null.</summary>
	public static async Task<Solution?> ChangedSolutionAsync(CodeAction action, CancellationToken cancellationToken = default)
	{
		ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(cancellationToken);
		foreach (CodeActionOperation operation in operations)
		{
			if (operation is ApplyChangesOperation apply)
				return apply.ChangedSolution;
		}

		return null;
	}

	public static string EncodeId(string documentPath, TextSpan span, DiscoveredAction action)
	{
		var actionRef = new ActionRef(documentPath, span.Start, span.Length, action.Kind, KeyOf(action.Action));
		return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(actionRef));
	}

	public static bool TryDecodeId(string actionId, out ActionRef actionRef)
	{
		try
		{
			actionRef = JsonSerializer.Deserialize<ActionRef>(Convert.FromBase64String(actionId))!;
			return actionRef is not null;
		}
		catch
		{
			actionRef = null!;
			return false;
		}
	}

	/// <summary>
	/// The compiled document a caller-supplied path names, through <see cref="DocumentPathResolver"/>: exact
	/// first, then a unique path suffix or file name.
	/// </summary>
	public static Document? FindDocument(Solution solution, string path) =>
		DocumentPathResolver.Resolve(solution, path) is { } resolved
			? solution.GetDocumentIdsWithFilePath(resolved.AbsolutePath)
				.Select(solution.GetDocument)
				.FirstOrDefault(document => document is not null)
			: null;

	public static TextSpan SpanFor(SourceText text, int line, int column, int? endLine, int? endColumn)
	{
		int start = Offset(text, line, column);
		int end = endLine is not null && endColumn is not null ? Offset(text, endLine.Value, endColumn.Value) : start;
		return TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
	}

	private static int Offset(SourceText text, int line, int column)
	{
		int lineIndex = Math.Clamp(line - 1, 0, text.Lines.Count - 1);
		TextLine textLine = text.Lines[lineIndex];
		int character = Math.Clamp(column - 1, 0, textLine.Span.Length);
		return textLine.Start + character;
	}

	/// <summary>
	/// The applicable actions under <paramref name="action"/>: itself, or - for a group such as "Add using"
	/// offering several namespaces - its nested actions, since a group produces no changes of its own.
	/// </summary>
	private static IEnumerable<CodeAction> Leaves(CodeAction action) =>
		action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Leaves);

	/// <summary>The identity an actionId carries for an action: its equivalence key, or its title when it has none.</summary>
	public static string KeyOf(CodeAction action) => action.EquivalenceKey ?? action.Title;
}
