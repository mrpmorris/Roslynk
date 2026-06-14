namespace Morris.Roslynk.Infrastructure.Provenance;

/// <summary>
/// A source path paired with its provenance, so editability and origin are never guessed from the
/// path. <see cref="SourcePath"/> is null for <see cref="SourceType.Metadata"/>.
/// </summary>
public sealed record SourceLocation(string? SourcePath, SourceType SourceType);
