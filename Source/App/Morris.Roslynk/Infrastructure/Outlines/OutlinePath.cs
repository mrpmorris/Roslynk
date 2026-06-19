namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Splits a forward-slash outline path into its folder and file-name parts, so a nested body can print a
/// shared folder once and nest each file beneath it instead of repeating the folder on every sibling. A path
/// with no directory part (a file at the solution root, or a synthetic bucket such as &lt;metadata&gt;) has a
/// null folder and is printed as a single line.
/// </summary>
internal static class OutlinePath
{
	public static (string? Folder, string Name) Split(string path)
	{
		int slash = path.LastIndexOf('/');
		return slash < 0 ? (null, path) : (path[..slash], path[(slash + 1)..]);
	}
}