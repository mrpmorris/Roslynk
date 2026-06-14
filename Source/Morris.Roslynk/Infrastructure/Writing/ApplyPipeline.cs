using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Persists a changed <see cref="Solution"/> to disk under the instance's single-writer lock. For each
/// changed document: re-hash the file on disk against what was loaded (reject as stale if it moved),
/// then commit via atomic <see cref="File.Replace(string, string, string)"/>. If any file in the batch
/// fails to commit, the already-swapped files are restored from their backups, so the batch is
/// all-or-nothing within the process.
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
			List<StagedFile> staged = await StageAsync(instance.CurrentSolution, updated, cancellationToken);
			Commit(staged);
			instance.AdvanceTo(updated);
			return staged.Select(file => file.Path).ToArray();
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

	private static async Task<List<StagedFile>> StageAsync(Solution current, Solution updated, CancellationToken cancellationToken)
	{
		var staged = new List<StagedFile>();
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
				if (!HashesEqual(loadedText, diskText))
					throw new StaleWriteException(path);

				string updatedText = (await updatedDocument.GetTextAsync(cancellationToken)).ToString();
				string tempPath = path + ".roslynk.tmp";
				await File.WriteAllTextAsync(tempPath, updatedText, cancellationToken);

				staged.Add(new StagedFile(path, tempPath, path + ".roslynk.bak"));
			}
		}

		return staged;
	}

	private static void Commit(List<StagedFile> staged)
	{
		var committed = new List<StagedFile>();
		try
		{
			foreach (StagedFile file in staged)
			{
				File.Replace(file.TempPath, file.Path, file.BackupPath);
				committed.Add(file);
			}
		}
		catch
		{
			foreach (StagedFile file in committed)
			{
				if (File.Exists(file.BackupPath))
					File.Replace(file.BackupPath, file.Path, destinationBackupFileName: null);
			}
			throw;
		}
		finally
		{
			foreach (StagedFile file in staged)
			{
				TryDelete(file.BackupPath);
				TryDelete(file.TempPath);
			}
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Best-effort cleanup of temp/backup files.
		}
	}

	private static bool HashesEqual(string left, string right) =>
		string.Equals(Hash(left), Hash(right), StringComparison.Ordinal);

	private static string Hash(string text) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private readonly record struct StagedFile(string Path, string TempPath, string BackupPath);
}
