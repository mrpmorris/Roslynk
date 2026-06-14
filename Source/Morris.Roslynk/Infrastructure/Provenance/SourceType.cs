namespace Morris.Roslynk.Infrastructure.Provenance;

/// <summary>
/// Where a piece of source came from, and therefore whether it is writable. Only <see cref="Source"/>
/// is writable; the write tools enforce that at the boundary, not on trust.
/// </summary>
public enum SourceType
{
	/// <summary>In-solution C# working-tree document (includes <c>*.Designer.cs</c>). Writable.</summary>
	Source,

	/// <summary>Build/source-generated document (incl. <c>*.razor.g.cs</c>). Read-only.</summary>
	Generated,

	/// <summary>Metadata-as-source pseudo-file. Read-only, non-literal. Reserved (deferred).</summary>
	Decompiled,

	/// <summary>Origin source fetched via SourceLink. Read-only, literal. Reserved (deferred).</summary>
	SourceLink,

	/// <summary>Signatures / XML docs only, no body; no source path.</summary>
	Metadata
}
