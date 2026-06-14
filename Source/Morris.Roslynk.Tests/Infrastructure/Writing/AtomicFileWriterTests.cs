using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Infrastructure.Writing;

public class AtomicFileWriterTests
{
	[Fact]
	public async Task WhenWritingABatch_ThenEveryFileGetsItsNewContentAndTempsAreCleanedUp()
	{
		string directory = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
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
}
