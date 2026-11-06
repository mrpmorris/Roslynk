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
}
