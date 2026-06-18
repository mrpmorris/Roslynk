using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Persists a changed <see cref="Solution"/> to disk under the instance's single-writer lock. For each
/// changed document it re-hashes the file on disk against what was loaded (rejecting as stale if it
/// moved), then hands the batch to <see cref="AtomicFileWriter"/> for an all-or-nothing commit before
/// advancing the in-memory snapshot.
/// </summary>
public sealed class ApplyPipeline
{
	public Task<IReadOnlyList<string>> ApplyAsync(RoslynInstance instance, Solution updated, CancellationToken cancellationToken = default)
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
			IReadOnlyList<PendingWrite> writes = await BuildWritesAsync(current, updated, token);
			await AtomicFileWriter.WriteAllAsync(writes, token);
			activity?.SetTag("roslynk.changed.count", writes.Count);
			return new WriteResult(updated, writes.Select(write => write.FilePath).ToArray());
		}, cancellationToken);
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
				Document currentDocument = current.GetDocument(documentId)!;

				string? path = updatedDocument.FilePath;
				if (path is null)
					continue;

				// Generated documents (the Razor .g.cs we add to the model) have no real file on disk and must
				// never be written back; they are regenerated on the next load.
				if (IsGenerated(path))
					continue;

				// A file shared across target-framework projects appears as several documents; write it once.
				if (!seen.Add(path))
					continue;

				string loadedText = (await currentDocument.GetTextAsync(cancellationToken)).ToString();
				string diskText = await File.ReadAllTextAsync(path, cancellationToken);
				if (!string.Equals(FileHash.Of(loadedText), FileHash.Of(diskText), StringComparison.Ordinal))
					throw new StaleWriteException(path);

				string updatedText = (await updatedDocument.GetTextAsync(cancellationToken)).ToString();
				writes.Add(new PendingWrite(path, updatedText));
			}
		}

		return writes;
	}
}
