namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The filesystem seam plugin discovery depends on, so layouts can be staged without a real disk in
/// tests. Keep it minimal: only what discovery needs. Deployment commands get their own operations
/// when they are implemented.
/// </summary>
public interface IFileSystem
{
	bool DirectoryExists(string path);

	/// <summary>The names (not full paths) of the immediate child directories, in ordinal order.</summary>
	IReadOnlyList<string> GetDirectoryNames(string path);

	bool FileExists(string path);

	/// <summary>The names (not full paths) of the immediate child files, in ordinal order.</summary>
	IReadOnlyList<string> GetFileNames(string path);
}
