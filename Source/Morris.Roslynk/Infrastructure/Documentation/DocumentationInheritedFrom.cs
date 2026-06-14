namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// Where <c>&lt;inheritdoc/&gt;</c> documentation was resolved from: the base symbol's fully-qualified
/// name, plus its location — a working-tree <see cref="SourcePath"/> when in this solution, otherwise the
/// <see cref="Assembly"/> name when the base is external metadata.
/// </summary>
public sealed record DocumentationInheritedFrom(string Symbol, string? SourcePath, string? Assembly);
