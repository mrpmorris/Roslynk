namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>A file's full new text, staged for an atomic batch write.</summary>
public readonly struct PendingWrite
{
	public string FilePath { get; }
	public string Text { get; }

	public PendingWrite(string filePath, string text)
	{
		FilePath = filePath;
		Text = text;
	}
}
