using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Renaming;

/// <summary>
/// A symbol to rename, resolved inside the projection solution it belongs to.
/// </summary>
public sealed record RenameTarget(Solution ProjectionSolution, ISymbol Symbol);

/// <summary>
/// Runs Roslyn's semantic rename for symbols resolved in one or more projections and folds the result back
/// onto the base solution: each projection rewrites only its own active branch, the per-file text changes are
/// unioned (deduped by span), Razor-generated changes are mapped to their .razor/.cshtml sources, and every
/// document sharing a physical file receives the same edits.
/// </summary>
public static class ProjectionRenamer
{
	/// <summary>
	/// Returns the base solution with every target renamed to <paramref name="newName"/>.
	/// Throws <see cref="RazorMappingException"/> when a Razor-generated change cannot be mapped and verified,
	/// and <see cref="RenameConflictException"/> when the unioned edits overlap; nothing is applied in either case.
	/// </summary>
	public static async Task<Solution> RenameAsync(
		Solution baseSolution,
		IEnumerable<RenameTarget> targets,
		string newName,
		CancellationToken cancellationToken = default)
	{
		// Rename in each projection — each rewrites only its own active branch, semantically — then union the
		// per-file text changes (deduped by span), so a symbol used in both #if and #else branches, or across
		// target frameworks, is rewritten everywhere rather than leaving the inactive branch stale.
		var changesByPath = new Dictionary<string, Dictionary<TextSpan, TextChange>>(StringComparer.OrdinalIgnoreCase);
		foreach (RenameTarget target in targets)
		{
			Solution projectionSolution = target.ProjectionSolution;
			Solution renamed = await Renamer.RenameSymbolAsync(projectionSolution, target.Symbol, new SymbolRenameOptions(), newName, cancellationToken);

			foreach (ProjectChanges projectChanges in renamed.GetChanges(projectionSolution).GetProjectChanges())
			{
				foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
				{
					Document originalDocument = projectionSolution.GetDocument(documentId)!;
					if (originalDocument.FilePath is not string path)
						continue;

					Document renamedDocument = renamed.GetDocument(documentId)!;
					if (!changesByPath.TryGetValue(path, out Dictionary<TextSpan, TextChange>? perSpan))
					{
						perSpan = [];
						changesByPath[path] = perSpan;
					}

					foreach (TextChange change in await renamedDocument.GetTextChangesAsync(originalDocument, cancellationToken))
						perSpan[change.Span] = change;
				}
			}
		}

		// Changes that landed in Razor-generated .g.cs documents can never be persisted; map each one back
		// to the .razor/.cshtml source it came from (via the compiler's #line mapping) so that file is
		// edited instead. Mapping failures abort here, before anything is applied or written.
		Dictionary<string, Dictionary<TextSpan, TextChange>> razorChangesByPath = await MapRazorGeneratedChangesAsync(baseSolution, changesByPath);

		// Apply the unioned changes onto the base solution (every document that shares the file), so the write
		// path persists each file once and the published snapshot reflects all branches. Razor-generated
		// documents keep their in-memory rename here too, so the published snapshot stays consistent with the
		// rewritten .razor sources without re-running the generator.
		Solution updated = baseSolution;
		foreach ((string path, Dictionary<TextSpan, TextChange> perSpan) in changesByPath)
		{
			List<TextChange> ordered = Ordered(path, perSpan);
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(path))
			{
				SourceText text = await baseSolution.GetDocument(documentId)!.GetTextAsync(cancellationToken);
				updated = updated.WithDocumentText(documentId, text.WithChanges(ordered));
			}
		}

		foreach ((string razorPath, Dictionary<TextSpan, TextChange> perSpan) in razorChangesByPath)
		{
			List<TextChange> ordered = Ordered(razorPath, perSpan);
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(razorPath))
			{
				if (baseSolution.GetAdditionalDocument(documentId) is not TextDocument additional)
					continue;

				SourceText text = await additional.GetTextAsync(cancellationToken);
				updated = updated.WithAdditionalDocumentText(documentId, text.WithChanges(ordered));
			}
		}

		return updated;
	}

	private static List<TextChange> Ordered(string path, Dictionary<TextSpan, TextChange> perSpan)
	{
		List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ThenBy(change => change.Span.End).ToList();
		for (int index = 1; index < ordered.Count; index++)
		{
			if (ordered[index].Span.Start < ordered[index - 1].Span.End)
				throw new RenameConflictException(path, $"The rename produced overlapping edits in '{path}'; nothing was applied.");
		}

		return ordered;
	}

	/// <summary>
	/// Maps the changes that landed in Razor-generated documents back to their .razor/.cshtml sources,
	/// keyed by razor file path and deduped by span (the same razor edit arrives once per projection and
	/// per generated copy). Throws <see cref="RazorMappingException"/> when any change cannot be mapped
	/// and verified, so the caller aborts without a partial rename.
	/// </summary>
	private static async Task<Dictionary<string, Dictionary<TextSpan, TextChange>>> MapRazorGeneratedChangesAsync(
		Solution baseSolution,
		Dictionary<string, Dictionary<TextSpan, TextChange>> changesByPath)
	{
		var razorChangesByPath = new Dictionary<string, Dictionary<TextSpan, TextChange>>(StringComparer.OrdinalIgnoreCase);

		async Task<SourceText?> RazorTextFor(string razorPath)
		{
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(razorPath))
			{
				if (baseSolution.GetAdditionalDocument(documentId) is TextDocument additional)
					return await additional.GetTextAsync();
			}

			return null;
		}

		foreach ((string path, Dictionary<TextSpan, TextChange> perSpan) in changesByPath)
		{
			if (!RazorMapping.IsRazorGeneratedPath(path))
				continue;

			Document? generatedDocument = baseSolution.GetDocumentIdsWithFilePath(path)
				.Select(baseSolution.GetDocument)
				.FirstOrDefault(document => document is not null);
			if (generatedDocument is null)
				continue;

			List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ToList();
			foreach ((string razorPath, TextChange change) in await RazorChangeMapper.MapChangesAsync(generatedDocument, ordered, RazorTextFor))
			{
				if (!razorChangesByPath.TryGetValue(razorPath, out Dictionary<TextSpan, TextChange>? razorPerSpan))
				{
					razorPerSpan = [];
					razorChangesByPath[razorPath] = razorPerSpan;
				}

				if (razorPerSpan.TryGetValue(change.Span, out TextChange existing) && !string.Equals(existing.NewText, change.NewText, StringComparison.Ordinal))
					throw new RazorMappingException(
						RazorMappingFailure.TextMismatch,
						path,
						$"Conflicting edits mapped to the same location in '{razorPath}'; the rename was not applied.");

				razorPerSpan[change.Span] = change;
			}
		}

		return razorChangesByPath;
	}
}

/// <summary>
/// Thrown when unioned rename edits for one file overlap and cannot be applied together.
/// </summary>
public sealed class RenameConflictException : Exception
{
	public string FilePath { get; }

	public RenameConflictException(string filePath, string message)
		: base(message)
	{
		FilePath = filePath;
	}
}
