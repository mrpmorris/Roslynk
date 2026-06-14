namespace Morris.Roslynk.Infrastructure.CodeActions;

/// <summary>
/// The stateless identity of a discovered action, encoded into the <c>actionId</c> a client passes back:
/// the document, the span it was found at, its kind, and its equivalence key (or title). Apply re-runs
/// discovery at that document and span and matches on kind + key, so nothing is held between calls.
/// </summary>
public sealed record ActionRef(string DocumentPath, int SpanStart, int SpanLength, string Kind, string Key);
