using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Infrastructure.Writing;

public class AtomicFileWriterTests : MutatingTestBase
{
	[Fact]
	public async Task WhenWritingABatch_ThenEveryFileGetsItsNewContentAndTempsAreCleanedUp()
	{
		string directory = Scratch(() =>
		{
			string path = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(path);
			return path;
		});
		string first = Path.Combine(directory, "first.txt");
		string second = Path.Combine(directory, "second.txt");
		await File.WriteAllTextAsync(first, "old-first");
		await File.WriteAllTextAsync(second, "old-second");

		await AtomicFileWriter.WriteAllAsync(
		[
			new PendingWrite(first, "new-first"),
			new PendingWrite(second, "new-second"),
		]);

		Assert.Equal("new-first", await File.ReadAllTextAsync(first));
		Assert.Equal("new-second", await File.ReadAllTextAsync(second));
		Assert.False(File.Exists(first + ".roslynk.tmp"));
		Assert.False(File.Exists(first + ".roslynk.bak"));
	}

	[Fact]
	public async Task WhenABatchWritesTheSamePathTwice_ThenItIsRejectedBeforeAnythingIsStaged()
	{
		string directory = Scratch(() =>
		{
			string path = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(path);
			return path;
		});
		string target = Path.Combine(directory, "target.txt");
		await File.WriteAllTextAsync(target, "original");

		await Assert.ThrowsAsync<ArgumentException>(() => AtomicFileWriter.WriteAllAsync(
		[
			new PendingWrite(target, "first"),
			new PendingWrite(target, "second"),
		]));

		Assert.Equal("original", await File.ReadAllTextAsync(target));
		Assert.False(File.Exists(target + ".roslynk.tmp"));
	}

	[Fact]
	public async Task WhenTheThirdReplaceFails_ThenEarlierFilesAreRestoredAndNoSiblingsRemain()
	{
		string directory = Scratch(CreateScratchDirectory);
		(string a, string b, string c) = await WriteOldFiles(directory);

		IOException failure = await Assert.ThrowsAsync<IOException>(() =>
			AtomicFileWriter.WriteAllAsync(
			[
				new PendingWrite(a, "new-a"),
				new PendingWrite(b, "new-b"),
				new PendingWrite(c, "new-c"),
			],
			Failing((source, _) => source == c + AtomicFileWriter.TempFileSuffix),
			CancellationToken.None));

		Assert.Equal($"Injected failure replacing '{c}'.", failure.Message);
		Assert.Equal("old-a", await File.ReadAllTextAsync(a));
		Assert.Equal("old-b", await File.ReadAllTextAsync(b));
		Assert.Equal("old-c", await File.ReadAllTextAsync(c));
		AssertSiblingsAreAbsent(a, b, c);
	}

	[Fact]
	public async Task WhenARestoreFails_ThenOtherFilesAreStillRestoredAndTheUnrestoredBackupIsKept()
	{
		string directory = Scratch(CreateScratchDirectory);
		(string a, string b, string c) = await WriteOldFiles(directory);

		AtomicWriteRollbackException failure = await Assert.ThrowsAsync<AtomicWriteRollbackException>(() =>
			AtomicFileWriter.WriteAllAsync(
			[
				new PendingWrite(a, "new-a"),
				new PendingWrite(b, "new-b"),
				new PendingWrite(c, "new-c"),
			],
			Failing((source, _) =>
				source == c + AtomicFileWriter.TempFileSuffix || source == a + AtomicFileWriter.BackupFileSuffix),
			CancellationToken.None));

		Assert.Equal(new[] { a }, failure.UnrestoredPaths);
		Assert.Contains(a + AtomicFileWriter.BackupFileSuffix, failure.Message);
		Assert.Equal("new-a", await File.ReadAllTextAsync(a));
		Assert.Equal("old-a", await File.ReadAllTextAsync(a + AtomicFileWriter.BackupFileSuffix));
		Assert.Equal("old-b", await File.ReadAllTextAsync(b));
		Assert.Equal("old-c", await File.ReadAllTextAsync(c));
		foreach (string path in new[] { a, b, c })
			Assert.False(File.Exists(path + AtomicFileWriter.TempFileSuffix));
		Assert.False(File.Exists(b + AtomicFileWriter.BackupFileSuffix));
		Assert.False(File.Exists(c + AtomicFileWriter.BackupFileSuffix));
	}

	[Fact]
	public async Task WhenTheRestoreOfTheLastCommittedFileFails_ThenEarlierCommittedFilesAreStillRestored()
	{
		string directory = Scratch(CreateScratchDirectory);
		(string a, string b, string c) = await WriteOldFiles(directory);

		AtomicWriteRollbackException failure = await Assert.ThrowsAsync<AtomicWriteRollbackException>(() =>
			AtomicFileWriter.WriteAllAsync(
			[
				new PendingWrite(a, "new-a"),
				new PendingWrite(b, "new-b"),
				new PendingWrite(c, "new-c"),
			],
			Failing((source, _) =>
				source == c + AtomicFileWriter.TempFileSuffix || source == b + AtomicFileWriter.BackupFileSuffix),
			CancellationToken.None));

		Assert.Equal(new[] { b }, failure.UnrestoredPaths);
		Assert.Contains(b + AtomicFileWriter.BackupFileSuffix, failure.Message);
		Assert.Equal("old-a", await File.ReadAllTextAsync(a));
		Assert.Equal("new-b", await File.ReadAllTextAsync(b));
		Assert.Equal("old-b", await File.ReadAllTextAsync(b + AtomicFileWriter.BackupFileSuffix));
		Assert.Equal("old-c", await File.ReadAllTextAsync(c));
		foreach (string path in new[] { a, b, c })
			Assert.False(File.Exists(path + AtomicFileWriter.TempFileSuffix));
		Assert.False(File.Exists(a + AtomicFileWriter.BackupFileSuffix));
		Assert.False(File.Exists(c + AtomicFileWriter.BackupFileSuffix));
	}

	[Fact]
	public async Task WhenAReplaceLeavesTheTargetMissing_ThenTheBackupIsMovedBack()
	{
		string directory = Scratch(CreateScratchDirectory);
		(string a, string b, string c) = await WriteOldFiles(directory);

		IOException failure = await Assert.ThrowsAsync<IOException>(() =>
			AtomicFileWriter.WriteAllAsync(
			[
				new PendingWrite(a, "new-a"),
				new PendingWrite(b, "new-b"),
				new PendingWrite(c, "new-c"),
			],
			(source, destination, backup) =>
			{
				if (source != c + AtomicFileWriter.TempFileSuffix)
				{
					File.Replace(source, destination, backup);
					return;
				}

				// Simulate Win32 ERROR_UNABLE_TO_MOVE_REPLACEMENT_2: the original is parked at the
				// backup path but the new content never takes its place.
				File.Move(destination, backup!);
				throw new IOException($"Injected failure replacing '{destination}'.");
			},
			CancellationToken.None));

		Assert.Equal($"Injected failure replacing '{c}'.", failure.Message);
		Assert.Equal("old-c", await File.ReadAllTextAsync(c));
		Assert.Equal("old-a", await File.ReadAllTextAsync(a));
		Assert.Equal("old-b", await File.ReadAllTextAsync(b));
		AssertSiblingsAreAbsent(a, b, c);
	}

	private static string CreateScratchDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static async Task<(string A, string B, string C)> WriteOldFiles(string directory)
	{
		string a = Path.Combine(directory, "a.txt");
		string b = Path.Combine(directory, "b.txt");
		string c = Path.Combine(directory, "c.txt");
		await File.WriteAllTextAsync(a, "old-a");
		await File.WriteAllTextAsync(b, "old-b");
		await File.WriteAllTextAsync(c, "old-c");
		return (a, b, c);
	}

	private static AtomicFileWriter.ReplaceFile Failing(Func<string, string, bool> shouldFail) =>
		(source, destination, backup) =>
		{
			if (shouldFail(source, destination))
				throw new IOException($"Injected failure replacing '{destination}'.");

			File.Replace(source, destination, backup);
		};

	private static void AssertSiblingsAreAbsent(params string[] paths)
	{
		foreach (string path in paths)
		{
			Assert.False(File.Exists(path + AtomicFileWriter.TempFileSuffix));
			Assert.False(File.Exists(path + AtomicFileWriter.BackupFileSuffix));
		}
	}
}
