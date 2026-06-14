namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>A file's full new text, staged for an atomic batch write.</summary>
public readonly record struct PendingWrite(string Path, string Text);
