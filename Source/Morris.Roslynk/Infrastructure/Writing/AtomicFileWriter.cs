namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Commits a batch of full-file rewrites atomically. Each file is written to a temp copy first, then
/// swapped in with <see cref="File.Replace(string, string, string)"/> (atomic per file, no torn reads),
/// keeping a backup. If any swap in the batch fails, the already-swapped files are restored from their
/// backups, so the batch is all-or-nothing within the process.
/// </summary>
public static class AtomicFileWriter
{
	public static async Task WriteAllAsync(IReadOnlyList<PendingWrite> writes, CancellationToken cancellationToken = default)
	{
		var staged = new List<StagedFile>();
		foreach (PendingWrite write in writes)
		{
			string tempPath = write.FilePath + ".roslynk.tmp";
			await File.WriteAllTextAsync(tempPath, write.Text, cancellationToken);
			staged.Add(new StagedFile(write.FilePath, tempPath, write.FilePath + ".roslynk.bak"));
		}

		Commit(staged);
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

	private readonly record struct StagedFile(string Path, string TempPath, string BackupPath);
}
