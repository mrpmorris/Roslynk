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
	private readonly string SortKey;
	private readonly int SortLine;
	private readonly int SortColumn;

	public SymbolNode() : this("")
	{
	}

	private SymbolNode(string key) : this(key, key, 0, 0)
	{
	}

	private SymbolNode(string key, string sortKey, int sortLine, int sortColumn)
	{
		Key = key;
		SortKey = sortKey;
		SortLine = sortLine;
		SortColumn = sortColumn;
	}

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

	/// <summary>
	/// Adds a leaf line '<paramref name="key"/>,&lt;loc&gt;,<paramref name="suffix"/>' carrying exactly one
	/// location, for outlines where each location has its own trailing field and so cannot share a pipe list.
	/// Leaves with the same key sort by position rather than by their text.
	/// </summary>
	public void AddLeaf(string key, int line, int column, int endLine, int endColumn, string suffix)
	{
		string text = $"{key},{LocationText((line, column, endLine, endColumn))},{suffix}";
		if (ChildIndex.ContainsKey(text))
			return;

		var leaf = new SymbolNode(text, key, line, column);
		ChildIndex.Add(text, leaf);
		Children.Add(leaf);
	}

	public void AddLocation(int line, int column, int endLine, int endColumn) =>
		Locations.Add((line, column, endLine, endColumn));

	/// <summary>Renders this node's children (the root itself has no line) sorted for determinism.</summary>
	public void Render(OutlineBuilder builder)
	{
		foreach (SymbolNode child in Ordered())
			child.RenderInto(builder, depth: 0);
	}

	private void RenderInto(OutlineBuilder builder, int depth)
	{
		builder.Line(depth, LineText());
		foreach (SymbolNode child in Ordered())
			child.RenderInto(builder, depth + 1);
	}

	private IEnumerable<SymbolNode> Ordered() =>
		Children
			.OrderBy(node => node.SortKey, StringComparer.Ordinal)
			.ThenBy(node => node.SortLine)
			.ThenBy(node => node.SortColumn)
			.ThenBy(node => node.Key, StringComparer.Ordinal);

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
