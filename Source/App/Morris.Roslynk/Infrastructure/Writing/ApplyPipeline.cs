using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Persists a changed <see cref="Solution"/> to disk under the instance's single-writer lock. For each
/// changed document (regular or additional — .razor/.cshtml) it re-hashes the file on disk against what
/// was loaded (rejecting as stale if it moved), then hands the batch to <see cref="AtomicFileWriter"/>
/// for an all-or-nothing commit before advancing the in-memory snapshot (with its Razor output regenerated).
/// </summary>
public sealed class ApplyPipeline
{
	public Task<IReadOnlyList<string>> ApplyAsync(RoslynInstance instance, Solution updated, CancellationToken cancellationToken = default) =>
		ApplyAsync(instance, updated, basedOn: null, cancellationToken);

	/// <summary>
	/// Persists <paramref name="updated"/>. When <paramref name="basedOn"/> (the snapshot the update was
	/// computed from) is given, only the documents the update changed relative to it are applied, onto the
	/// latest snapshot: an intervening publication that edited one of those documents (a watcher fold or
	/// another write) is refused with a <see cref="StaleWriteException"/>, while intervening edits to other
	/// documents are kept rather than reverted.
	/// </summary>
	public Task<IReadOnlyList<string>> ApplyAsync(RoslynInstance instance, Solution updated, Solution? basedOn, CancellationToken cancellationToken = default)
	{
		if (instance is null)
			throw new ArgumentNullException(nameof(instance));
		if (updated is null)
			throw new ArgumentNullException(nameof(updated));

		// The single-writer consumer runs this against the latest snapshot under the write lock, then
		// publishes the edited snapshot; the stale-write guard re-validates each file against disk.
		return instance.EnqueueWriteAsync(async (current, token) =>
		{
			using Activity? activity = RoslynkActivitySource.Instance.StartActivity("apply_changes");
			Solution target = basedOn is null ? updated : await RebaseAsync(current, basedOn, updated, token);
			IReadOnlyList<PendingWrite> writes = await BuildWritesAsync(current, target, token);
			await AtomicFileWriter.WriteAllAsync(writes, token);
			activity?.SetTag("roslynk.changed.count", writes.Count);

			// Disk is committed, so the model must follow it: regeneration is not cancelled from here on.
			target = await RegenerateRazorAsync(instance, current, target, CancellationToken.None);
			return new WriteResult(target, writes.Select(write => write.FilePath).ToArray());
		}, cancellationToken);
	}

	/// <summary>
	/// Replaces folded Razor approximations with real generator output as part of the same write. A rename
	/// reaching .razor files via the #line mapping edits the generated documents in memory as an
	/// approximation; regenerating here means the file watcher's later fold finds nothing to change, so it
	/// cannot invalidate a diagnostics cache built from the post-write state. Runs after the files are
	/// written, so a stale write never advances the generator state. As in the watcher's C# fold, an edit
	/// confined to method bodies or initializers cannot change generated code and regenerates nothing.
	/// </summary>
	private static async Task<Solution> RegenerateRazorAsync(RoslynInstance instance, Solution current, Solution target, CancellationToken token)
	{
		if (instance.Workspace?.Razor is not RazorGenerationState razor)
			return target;

		var changedProjects = new HashSet<ProjectId>();
		foreach (ProjectChanges projectChanges in target.GetChanges(current).GetProjectChanges())
		{
			if (projectChanges.GetChangedAdditionalDocuments().Any()
				|| projectChanges.GetAddedDocuments().Any()
				|| projectChanges.GetRemovedDocuments().Any()
				|| await AnyDeclarationChangedAsync(current, target, projectChanges.GetChangedDocuments(), token))
			{
				changedProjects.Add(projectChanges.ProjectId);
			}
		}
		if (changedProjects.Count == 0)
			return target;

		return await RazorDocumentGenerator.RegenerateAsync(target, changedProjects, razor, token);
	}

	/// <summary>True when a changed document is generated (it holds a folded approximation) or its declarations changed.</summary>
	private static async Task<bool> AnyDeclarationChangedAsync(Solution current, Solution target, IEnumerable<DocumentId> documentIds, CancellationToken token)
	{
		foreach (DocumentId documentId in documentIds)
		{
			Document before = current.GetDocument(documentId)!;
			if (before.FilePath is string path && IsGenerated(path))
				return true;

			if (await before.GetSyntaxTreeAsync(token) is not SyntaxTree oldTree
				|| await target.GetDocument(documentId)!.GetSyntaxTreeAsync(token) is not SyntaxTree newTree
				|| !newTree.IsEquivalentTo(oldTree, topLevel: true))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Replays the document edits <paramref name="updated"/> made to <paramref name="basedOn"/> onto
	/// <paramref name="current"/>, refusing any document whose text in <paramref name="current"/> is no longer
	/// the text the edit was computed from.
	/// </summary>
	private static async Task<Solution> RebaseAsync(Solution current, Solution basedOn, Solution updated, CancellationToken cancellationToken)
	{
		Solution target = current;
		foreach (ProjectChanges projectChanges in updated.GetChanges(basedOn).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				Document baseDocument = basedOn.GetDocument(documentId)!;
				if (baseDocument.FilePath is string generatedPath && IsGenerated(generatedPath))
				{
					// A generated document is never written to disk, so a regeneration since the edit was computed
					// is not a conflict: keep the regenerated text instead of replaying the in-memory edit onto it.
					Document? currentGenerated = current.GetDocument(documentId);
					if (currentGenerated is not null && (await currentGenerated.GetTextAsync(cancellationToken)).ContentEquals(await baseDocument.GetTextAsync(cancellationToken)))
						target = target.WithDocumentText(documentId, await updated.GetDocument(documentId)!.GetTextAsync(cancellationToken));
					continue;
				}

				await EnsureUnchangedAsync(current.GetDocument(documentId), basedOn.GetDocument(documentId)!, cancellationToken);
				target = target.WithDocumentText(documentId, await updated.GetDocument(documentId)!.GetTextAsync(cancellationToken));
			}

			foreach (DocumentId documentId in projectChanges.GetChangedAdditionalDocuments())
			{
				await EnsureUnchangedAsync(current.GetAdditionalDocument(documentId), basedOn.GetAdditionalDocument(documentId)!, cancellationToken);
				target = target.WithAdditionalDocumentText(documentId, await updated.GetAdditionalDocument(documentId)!.GetTextAsync(cancellationToken));
			}
		}

		return target;
	}

	private static async Task EnsureUnchangedAsync(TextDocument? currentDocument, TextDocument baseDocument, CancellationToken cancellationToken)
	{
		string path = baseDocument.FilePath ?? baseDocument.Name;
		if (currentDocument is null)
			throw new StaleWriteException(path, $"'{path}' was removed from the solution since the edit was computed; the edit was not applied.");

		SourceText currentText = await currentDocument.GetTextAsync(cancellationToken);
		SourceText baseText = await baseDocument.GetTextAsync(cancellationToken);
		if (!currentText.ContentEquals(baseText))
			throw new StaleWriteException(path, $"'{path}' changed since the edit was computed; the edit was not applied.");
	}

	/// <summary>The files an update would change, without writing anything (for previews / checkOnly).</summary>
	public static IReadOnlyList<string> GetChangedFilePaths(Solution current, Solution updated)
	{
		var paths = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ProjectChanges projectChanges in updated.GetChanges(current).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				string? path = updated.GetDocument(documentId)?.FilePath;
				if (path is not null && !IsGenerated(path) && seen.Add(path))
					paths.Add(path);
			}

			foreach (DocumentId documentId in projectChanges.GetChangedAdditionalDocuments())
			{
				string? path = updated.GetAdditionalDocument(documentId)?.FilePath;
				if (path is not null && seen.Add(path))
					paths.Add(path);
			}
		}

		return paths;
	}

	/// <summary>A generated document (a Razor .g.cs added to the model) is never persisted to disk.</summary>
	private static bool IsGenerated(string path) =>
		path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);

	private static async Task<IReadOnlyList<PendingWrite>> BuildWritesAsync(Solution current, Solution updated, CancellationToken cancellationToken)
	{
		var writes = new List<PendingWrite>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ProjectChanges projectChanges in updated.GetChanges(current).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				Document updatedDocument = updated.GetDocument(documentId)!;

				string? path = updatedDocument.FilePath;
				if (path is null)
					continue;

				// Generated documents (the Razor .g.cs we add to the model) have no real file on disk and must
				// never be written back; they are regenerated on the next load.
				if (IsGenerated(path))
					continue;

				await AddWriteAsync(writes, seen, path, current.GetDocument(documentId)!, updatedDocument, cancellationToken);
			}

			// Additional documents (.razor/.cshtml edited via the Razor #line mapping) are real source files
			// and get the same stale guard as regular documents.
			foreach (DocumentId documentId in projectChanges.GetChangedAdditionalDocuments())
			{
				TextDocument updatedDocument = updated.GetAdditionalDocument(documentId)!;
				if (updatedDocument.FilePath is not string path)
					continue;

				await AddWriteAsync(writes, seen, path, current.GetAdditionalDocument(documentId)!, updatedDocument, cancellationToken);
			}
		}

		return writes;
	}

	private static async Task AddWriteAsync(List<PendingWrite> writes, HashSet<string> seen, string path, TextDocument currentDocument, TextDocument updatedDocument, CancellationToken cancellationToken)
	{
		// A file shared across target-framework projects appears as several documents; write it once.
		if (!seen.Add(path))
			return;

		string loadedText = (await currentDocument.GetTextAsync(cancellationToken)).ToString();
		string diskText = await File.ReadAllTextAsync(path, cancellationToken);
		if (!string.Equals(FileHash.Of(loadedText), FileHash.Of(diskText), StringComparison.Ordinal))
			throw new StaleWriteException(path);

		string updatedText = (await updatedDocument.GetTextAsync(cancellationToken)).ToString();
		writes.Add(new PendingWrite(path, updatedText));
	}
}
