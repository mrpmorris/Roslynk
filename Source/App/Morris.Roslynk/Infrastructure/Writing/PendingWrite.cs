using System.Text;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>A file's full new text, staged for an atomic batch write. The encoding is the one the file was
/// read with (BOM and all), so a round-trip preserves it; null means plain UTF-8 without a BOM.</summary>
public readonly struct PendingWrite
{
	public string FilePath { get; }
	public string Text { get; }
	public Encoding? Encoding { get; }

	public PendingWrite(string filePath, string text, Encoding? encoding = null)
	{
		FilePath = filePath;
		Text = text;
		Encoding = encoding;
	}
}
