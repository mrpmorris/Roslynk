namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// Where <c>&lt;inheritdoc/&gt;</c> documentation was resolved from: the base symbol's fully-qualified
/// name, plus its location — a working-tree <see cref="SourcePath"/> when in this solution, otherwise the
/// <see cref="Assembly"/> name when the base is external metadata.
/// </summary>
public sealed class DocumentationInheritedFrom
{
	public string Symbol { get; }
	public string? SourcePath { get; }
	public string? Assembly { get; }

	public DocumentationInheritedFrom(string symbol, string? sourcePath, string? assembly)
	{
		Symbol = symbol;
		SourcePath = sourcePath;
		Assembly = assembly;
	}
}
