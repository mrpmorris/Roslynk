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

	/// <summary>Creates a directory (and its parents). Returns the created path.</summary>
	string CreateDirectory(string path);

	/// <summary>Deletes a directory and everything beneath it; a missing directory is not an error.</summary>
	void DeleteDirectory(string path);

	/// <summary>Copies a directory and everything beneath it into <paramref name="targetDirectory"/>, which must not exist.</summary>
	void CopyDirectory(string sourceDirectory, string targetDirectory);

	/// <summary>Renames (moves) a directory to <paramref name="targetDirectory"/>, which must not exist. Both paths
	/// are siblings on the same volume in every caller, so the rename is atomic.</summary>
	void RenameDirectory(string sourceDirectory, string targetDirectory);

	/// <summary>Deletes a file; a missing file is not an error.</summary>
	void DeleteFile(string path);
}
