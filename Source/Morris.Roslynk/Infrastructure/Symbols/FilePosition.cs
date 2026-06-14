namespace Morris.Roslynk.Infrastructure.Symbols;

/// <summary>
/// A 1-based caret location used to resolve a symbol when the caller has a position rather than an id.
/// </summary>
public readonly record struct FilePosition(string FilePath, int Line, int Column);
