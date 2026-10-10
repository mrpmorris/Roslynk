namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>One dot-separated segment of a name: its identifier and the generic arity written on it, or -1 when none was.</summary>
public readonly record struct SymbolNameSegment(string Name, int Arity);
