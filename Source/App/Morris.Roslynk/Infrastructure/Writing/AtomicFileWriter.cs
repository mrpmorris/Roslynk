namespace Morris.Roslynk.Infrastructure.Writing;

using System.Text;

/// <summary>
/// Commits a batch of full-file rewrites atomically. Each file is written to a temp copy first, then
/// swapped in with <see cref="File.Replace(string, string, string)"/> (atomic per file, no torn reads),
/// keeping a backup. If any swap in the batch fails, every already-swapped file is restored from its
/// backup on a best-effort basis: a restore that fails keeps that file's backup and is named by the
/// thrown <see cref="AtomicWriteRollbackException"/>.
/// </summary>
public static class AtomicFileWriter
{
	/// <summary>The suffix of the temp sibling a write is staged to before the swap. Staging files live
	/// next to the target inside watched directories, so the file watcher must treat this suffix (and
	/// <see cref="BackupFileSuffix"/>) as the server's own write activity, never a user edit.</summary>
	public const string TempFileSuffix = ".roslynk.tmp";

	/// <summary>The suffix of the backup sibling the previous content is parked at during the swap.</summary>
	public const string BackupFileSuffix = ".roslynk.bak";

	private static readonly Encoding DefaultUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	// File paths are compared with the filesystem's convention: case-insensitive on Windows and
	// macOS, case-sensitive elsewhere.
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	/// <summary>The file-replace operation, injectable so tests can make a chosen replace fail.</summary>
	internal delegate void ReplaceFile(string sourcePath, string destinationPath, string? backupPath);

	public static Task WriteAllAsync(IReadOnlyList<PendingWrite> writes, CancellationToken cancellationToken = default) =>
		WriteAllAsync(writes, File.Replace, cancellationToken);

	internal static async Task WriteAllAsync(IReadOnlyList<PendingWrite> writes, ReplaceFile replace, CancellationToken cancellationToken)
	{
		// Reject a duplicate path before staging anything: both writes would stage to the same temp
		// sibling and the second File.Replace would fail after the first had already consumed it.
		var seen = new HashSet<string>(PathComparer);
		foreach (PendingWrite write in writes)
		{
			if (!seen.Add(write.FilePath))
				throw new ArgumentException($"The batch writes '{write.FilePath}' more than once; each path must appear once.", nameof(writes));
		}

		var staged = new List<StagedFile>();
		foreach (PendingWrite write in writes)
		{
			string tempPath = write.FilePath + TempFileSuffix;
			await File.WriteAllTextAsync(tempPath, write.Text, write.Encoding ?? DefaultUtf8, cancellationToken);
			staged.Add(new StagedFile(write.FilePath, tempPath, write.FilePath + BackupFileSuffix));
		}

		Commit(staged, replace);
	}

	private static void Commit(List<StagedFile> staged, ReplaceFile replace)
	{
		var committed = new List<StagedFile>();
		var keepBackups = new HashSet<string>(StringComparer.Ordinal);
		int failingIndex = -1;
		try
		{
			for (int index = 0; index < staged.Count; index++)
			{
				failingIndex = index;
				StagedFile file = staged[index];
				replace(file.TempPath, file.Path, file.BackupPath);
				committed.Add(file);
			}

			failingIndex = -1;
		}
		catch (Exception commitFailure)
		{
			var unrestored = new List<string>();
			var restoreFailures = new List<Exception>();

			// The file whose own replace threw may have been moved to its backup without the new content
			// taking its place (Win32 ERROR_UNABLE_TO_MOVE_REPLACEMENT_2). Put the original back.
			if (failingIndex >= 0)
			{
				StagedFile failing = staged[failingIndex];
				if (!File.Exists(failing.Path) && File.Exists(failing.BackupPath))
				{
					try
					{
						File.Move(failing.BackupPath, failing.Path);
					}
					catch (Exception moveFailure)
					{
						keepBackups.Add(failing.BackupPath);
						unrestored.Add(failing.Path);
						restoreFailures.Add(moveFailure);
					}
				}
			}

			// Restore in reverse commit order; one failure must not stop the others.
			for (int index = committed.Count - 1; index >= 0; index--)
			{
				StagedFile file = committed[index];
				if (!File.Exists(file.BackupPath))
					continue;

				try
				{
					replace(file.BackupPath, file.Path, null);
				}
				catch (Exception restoreFailure)
				{
					keepBackups.Add(file.BackupPath);
					unrestored.Add(file.Path);
					restoreFailures.Add(restoreFailure);
				}
			}

			if (unrestored.Count > 0)
				throw new AtomicWriteRollbackException(unrestored, commitFailure, restoreFailures);

			throw;
		}
		finally
		{
			foreach (StagedFile file in staged)
			{
				if (!keepBackups.Contains(file.BackupPath))
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

	private readonly struct StagedFile
	{
		public string Path { get; }
		public string TempPath { get; }
		public string BackupPath { get; }

		public StagedFile(string path, string tempPath, string backupPath)
		{
			Path = path;
			TempPath = tempPath;
			BackupPath = backupPath;
		}
	}
}
