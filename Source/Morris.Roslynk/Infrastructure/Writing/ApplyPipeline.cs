using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Persists a changed <see cref="Solution"/> to disk under the instance's single-writer lock. For each
/// changed document it re-hashes the file on disk against what was loaded (rejecting as stale if it
/// moved), then hands the batch to <see cref="AtomicFileWriter"/> for an all-or-nothing commit before
/// advancing the in-memory snapshot.
/// </summary>
public sealed class ApplyPipeline
{
	public async Task<IReadOnlyList<string>> ApplyAsync(RoslynInstance instance, Solution updated, CancellationToken cancellationToken = default)
	{
		if (instance is null)
			throw new ArgumentNullException(nameof(instance));
		if (updated is null)
			throw new ArgumentNullException(nameof(updated));

		await instance.WriteLock.WaitAsync(cancellationToken);
		try
		{
			IReadOnlyList<PendingWrite> writes = await BuildWritesAsync(instance.CurrentSolution, updated, cancellationToken);
			await AtomicFileWriter.WriteAllAsync(writes, cancellationToken);
			instance.AdvanceTo(updated);
			return writes.Select(write => write.Path).ToArray();
		}
		finally
		{
			instance.WriteLock.Release();
		}
	}

	/// <summary>The files an update would change, without writing anything (for previews / checkOnly).</summary>
	public static IReadOnlyList<string> GetChangedFilePaths(Solution current, Solution updated)
	{
		var paths = new List<string>();
		foreach (ProjectChanges projectChanges in updated.GetChanges(current).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				string? path = updated.GetDocument(documentId)?.FilePath;
				if (path is not null)
					paths.Add(path);
			}
		}

		return paths;
	}

	private static async Task<IReadOnlyList<PendingWrite>> BuildWritesAsync(Solution current, Solution updated, CancellationToken cancellationToken)
	{
		var writes = new List<PendingWrite>();
		foreach (ProjectChanges projectChanges in updated.GetChanges(current).GetProjectChanges())
		{
			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				Document updatedDocument = updated.GetDocument(documentId)!;
				Document currentDocument = current.GetDocument(documentId)!;

				string? path = updatedDocument.FilePath;
				if (path is null)
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
