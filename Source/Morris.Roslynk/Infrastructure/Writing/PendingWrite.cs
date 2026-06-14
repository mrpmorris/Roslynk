namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>A file's full new text, staged for an atomic batch write.</summary>
public readonly struct PendingWrite
{
	public string Path { get; }
	public string Text { get; }

	public PendingWrite(string path, string text)
	{
		Path = path;
		Text = text;
	}
}
