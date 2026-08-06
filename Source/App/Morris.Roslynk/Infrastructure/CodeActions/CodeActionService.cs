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
/// solution it would produce. Fixes are driven from compiler <em>and</em> project-analyzer diagnostics
/// overlapping the span (same analyzer set as <c>get_diagnostics</c> with analyzers on); refactorings are
/// computed over the span.
/// </summary>
public sealed class CodeActionService
{
	private const int MaxActions = 50;

	private readonly DiagnosticsService DiagnosticsService;

	public CodeActionService(DiagnosticsService diagnosticsService)
	{
		DiagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
	}

	/// <summary>Test-friendly constructor; production code receives <see cref="DiagnosticsService"/> via DI.</summary>
	public CodeActionService()
		: this(new DiagnosticsService())
	{
	}

	public async Task<IReadOnlyList<DiscoveredAction>> DiscoverAsync(Document document, TextSpan span, CancellationToken cancellationToken = default)
	{
		var discovered = new List<DiscoveredAction>();

		// Same analyzer-aware source as get_diagnostics (includeAnalyzers: true): compiler CS* plus project
		// analyzer IDE*/CA* ids, so fix-by-id and get_code_actions can see what the list path reports.
		ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsForDocumentAsync(document, cancellationToken);
		diagnostics = diagnostics
			.Where(diagnostic => diagnostic.Location.IsInSource
				&& diagnostic.Location.SourceSpan.IntersectsWith(span))
			.ToImmutableArray();

		foreach (CodeFixProvider provider in CodeActionCatalog.Instance.FixProviders)
		{
			ImmutableArray<string> fixable = SafeFixableIds(provider);
			foreach (Diagnostic diagnostic in diagnostics)
			{
				// Some IDE diagnostics are "classification" ids (e.g. IDE0005) while the fix provider only
				// claims a related fixable id (RemoveUnnecessaryImportsFixable). Map before RegisterCodeFixes.
				Diagnostic forProvider = MapToProviderDiagnostic(diagnostic, fixable);
				if (!fixable.Contains(forProvider.Id))
					continue;

				var registered = new List<CodeAction>();
				var context = new CodeFixContext(document, forProvider, (action, _) => registered.Add(action), cancellationToken);
				try
				{
					await provider.RegisterCodeFixesAsync(context);
				}
				catch
				{
					continue;
				}

				// Keep the original (agent-visible) id on the action so apply_code_fix(IDE0005) matches.
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

	/// <summary>
	/// First diagnostic with <paramref name="diagnosticId"/> in <paramref name="document"/>, using the same
	/// analyzer-aware set as discovery / <c>get_diagnostics</c>. Used by <c>apply_code_fix</c>.
	/// Also matches known fixable aliases (e.g. <c>RemoveUnnecessaryImportsFixable</c> for IDE0005).
	/// </summary>
	public async Task<Diagnostic?> FindDiagnosticAsync(Document document, string diagnosticId, CancellationToken cancellationToken = default)
	{
		ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsForDocumentAsync(document, cancellationToken);
		Diagnostic? exact = diagnostics.FirstOrDefault(diagnostic =>
			string.Equals(diagnostic.Id, diagnosticId, StringComparison.Ordinal));
		if (exact is not null)
			return exact;

		// Prefer the agent-visible classification id when the caller asked for it via a related id, or the reverse.
		foreach (string related in RelatedDiagnosticIds(diagnosticId))
		{
			Diagnostic? match = diagnostics.FirstOrDefault(diagnostic =>
				string.Equals(diagnostic.Id, related, StringComparison.Ordinal));
			if (match is not null)
				return match;
		}

		return null;
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

	/// <summary>
	/// Analyzer-aware diagnostics for one document (project-scoped compile + analyzers, filtered to the
	/// document's tree). Matches <c>get_diagnostics</c> with analyzers on for that file's project.
	/// </summary>
	private async Task<ImmutableArray<Diagnostic>> GetDiagnosticsForDocumentAsync(Document document, CancellationToken cancellationToken)
	{
		SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
		if (tree is null)
			return [];

		IReadOnlyList<Diagnostic> all = await DiagnosticsService.GetProjectDiagnosticsAsync(
			document.Project,
			includeAnalyzers: true,
			cancellationToken);

		return all
			.Where(diagnostic => diagnostic.Location.IsInSource && diagnostic.Location.SourceTree == tree)
			.ToImmutableArray();
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

	/// <summary>
	/// Ids that share a fix with <paramref name="diagnosticId"/> when the analyzer reports a display id and a
	/// separate fixable id (Roslyn's unnecessary-imports pattern).
	/// </summary>
	internal static IEnumerable<string> RelatedDiagnosticIds(string diagnosticId)
	{
		if (diagnosticId is "IDE0005" or "IDE0005_gen")
		{
			yield return "RemoveUnnecessaryImportsFixable";
			yield break;
		}

		if (diagnosticId == "RemoveUnnecessaryImportsFixable")
		{
			yield return "IDE0005";
			yield return "IDE0005_gen";
		}
	}

	/// <summary>
	/// When a provider does not claim the classification id but claims a known fixable alias, return a diagnostic
	/// with that alias id at the same location so <see cref="CodeFixProvider.RegisterCodeFixesAsync"/> accepts it.
	/// </summary>
	private static Diagnostic MapToProviderDiagnostic(Diagnostic diagnostic, ImmutableArray<string> fixableIds)
	{
		if (fixableIds.Contains(diagnostic.Id))
			return diagnostic;

		foreach (string related in RelatedDiagnosticIds(diagnostic.Id))
		{
			if (!fixableIds.Contains(related))
				continue;

			// Preserve location and message; providers typically gate only on Id.
			var descriptor = new DiagnosticDescriptor(
				related,
				diagnostic.Descriptor.Title,
				diagnostic.GetMessage(),
				diagnostic.Descriptor.Category,
				diagnostic.Severity,
				isEnabledByDefault: true);
			return Diagnostic.Create(descriptor, diagnostic.Location);
		}

		return diagnostic;
	}
}
