namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// The enclosing namespace plus the ordered segment chain for a source location, from the outermost
/// containing type down to the declaration that lexically contains the location (a member, or the type
/// itself when the location sits at type level, e.g. a base list). Used to nest references under
/// file -> namespace -> type(s) -> member.
/// </summary>
public sealed class EnclosingPath
{
	public string Namespace { get; }
	public IReadOnlyList<EnclosingSegment> Segments { get; }

	public EnclosingPath(string @namespace, IReadOnlyList<EnclosingSegment> segments)
	{
		Namespace = @namespace;
		Segments = segments;
	}
}
