namespace Morris.Roslynk.Infrastructure.Symbols;

/// <summary>
/// A 1-based caret location used to resolve a symbol when the caller has a position rather than an id.
/// </summary>
public readonly struct FilePosition
{
	public string FilePath { get; }
	public int Line { get; }
	public int Column { get; }

	public FilePosition(string filePath, int line, int column)
	{
		FilePath = filePath;
		Line = line;
		Column = column;
	}
}
