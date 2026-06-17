namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// A node in a symbol outline tree, rendered as tab-indented lines. Children are keyed by their line text so
/// repeated paths (file -> namespace -> type -> nested type -> member) collapse to a single shared parent.
/// A node carries optional locations appended after its key as a pipe-delimited, position-sorted list; a
/// node that only parents children renders its key alone. Build a tree by walking <see cref="Child"/> from
/// the root, attach locations at the leaf, then <see cref="Render"/> it into an <see cref="OutlineBuilder"/>.
/// </summary>
public sealed class SymbolNode
{
	private readonly string Key;
	private readonly Dictionary<string, SymbolNode> ChildIndex = new(StringComparer.Ordinal);
	private readonly List<SymbolNode> Children = [];
	private readonly List<(int Line, int Column, int EndLine, int EndColumn)> Locations = [];

	public SymbolNode() : this("")
	{
	}

	private SymbolNode(string key) => Key = key;

	/// <summary>Returns the child with the exact line text, creating it on first use.</summary>
	public SymbolNode Child(string key)
	{
		if (!ChildIndex.TryGetValue(key, out SymbolNode? child))
		{
			child = new SymbolNode(key);
			ChildIndex.Add(key, child);
			Children.Add(child);
		}

		return child;
	}

	/// <summary>
	/// Returns the leaf node for a file path, nesting it under a folder node when the path has a directory
	/// part (so siblings that share a folder collapse to a single folder line); a path with no directory part
	/// (a root-level file, or a synthetic bucket) is a direct child as-is.
	/// </summary>
	public SymbolNode ChildPath(string path)
	{
		(string? folder, string name) = OutlinePath.Split(path);
		return folder is null ? Child(name) : Child(folder).Child(name);
	}
	public void AddLocation(int line, int column, int endLine, int endColumn) =>
		Locations.Add((line, column, endLine, endColumn));

	/// <summary>Renders this node's children (the root itself has no line) sorted for determinism.</summary>
	public void Render(OutlineBuilder builder)
	{
		foreach (SymbolNode child in Children.OrderBy(node => node.Key, StringComparer.Ordinal))
			child.RenderInto(builder, depth: 0);
	}

	private void RenderInto(OutlineBuilder builder, int depth)
	{
		builder.Line(depth, LineText());
		foreach (SymbolNode child in Children.OrderBy(node => node.Key, StringComparer.Ordinal))
			child.RenderInto(builder, depth + 1);
	}

	private string LineText()
	{
		if (Locations.Count == 0)
			return Key;

		IEnumerable<string> rendered = Locations
			.OrderBy(location => location.Line)
			.ThenBy(location => location.Column)
			.Select(LocationText);

		return Key + "," + string.Join(OutlineBuilder.LocationSeparator, rendered);
	}

	private static string LocationText((int Line, int Column, int EndLine, int EndColumn) location) =>
		location.Line == location.EndLine
			? $"{location.Line}:{location.Column}"
			: $"{location.Line}:{location.Column}-{location.EndLine}:{location.EndColumn}";
}
