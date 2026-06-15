using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.CodeActions;

/// <summary>
/// Discovers code fixes and refactorings at a span and resolves a previously-discovered action back to the
/// solution it would produce. Fixes are driven from the compiler diagnostics overlapping the span (analyzer
/// diagnostics need a separate analyzer pass, layered on later); refactorings are computed over the span.
/// </summary>
public sealed class CodeActionService
{
	private const int MaxActions = 50;

	public async Task<IReadOnlyList<DiscoveredAction>> DiscoverAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		var discovered = new List<DiscoveredAction>();

		// Source diagnostics from the whole compilation (so compilation-completion diagnostics are included),
		// filtered to this document's tree and the span. The provider's FixableDiagnosticIds gates relevance,
		// so every severity is kept.
		Compilation? compilation = await document.Project.GetCompilationAsync(cancellationToken);
		SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
		ImmutableArray<Diagnostic> diagnostics = compilation is null || tree is null
			? []
			: compilation.GetDiagnostics(cancellationToken)
				.Where(diagnostic => diagnostic.Location.SourceTree == tree && diagnostic.Location.SourceSpan.IntersectsWith(span))
				.ToImmutableArray();

		foreach (CodeFixProvider provider in CodeActionCatalog.Instance.FixProviders)
		{
			ImmutableArray<string> fixable = SafeFixableIds(provider);
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

				foreach (CodeAction action in registered)
					discovered.Add(new DiscoveredAction(action, "Fix", diagnostic.Id));
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

			foreach (CodeAction action in registered)
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

	public static Document? FindDocument(Solution solution, string path)
	{
		string normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
		string full = SolutionRelativePath.ToAbsolute(SolutionRelativePath.DirectoryOf(solution), normalized);

		Document? suffixMatch = null;
		int suffixMatches = 0;
		foreach (Document document in solution.Projects.SelectMany(project => project.Documents))
		{
			if (document.FilePath is null)
				continue;
			if (string.Equals(document.FilePath, full, StringComparison.OrdinalIgnoreCase))
				return document;
			if (document.FilePath.EndsWith(System.IO.Path.DirectorySeparatorChar + normalized, StringComparison.OrdinalIgnoreCase))
			{
				suffixMatch = document;
				suffixMatches++;
			}
		}

		return suffixMatches == 1 ? suffixMatch : null;
	}

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

	private static string KeyOf(CodeAction action) => action.EquivalenceKey ?? action.Title;

	private static ImmutableArray<string> SafeFixableIds(CodeFixProvider provider)
	{
		try
		{
			return provider.FixableDiagnosticIds;
		}
		catch
		{
			return [];
		}
	}
}
