namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>The real-filesystem implementation of <see cref="IFileSystem"/>.</summary>
public sealed class DefaultFileSystem : IFileSystem
{
	public static readonly DefaultFileSystem Instance = new();

	public bool DirectoryExists(string path) =>
		Directory.Exists(path);

	public IReadOnlyList<string> GetDirectoryNames(string path) =>
		Directory.Exists(path)
			? Directory.EnumerateDirectories(path).Select(name => Path.GetFileName(name)!).OrderBy(name => name, StringComparer.Ordinal).ToArray()
			: [];

	public bool FileExists(string path) =>
		File.Exists(path);

	public IReadOnlyList<string> GetFileNames(string path) =>
		Directory.Exists(path)
			? Directory.EnumerateFiles(path).Select(name => Path.GetFileName(name)!).OrderBy(name => name, StringComparer.Ordinal).ToArray()
			: [];

	public string CreateDirectory(string path) =>
		Directory.CreateDirectory(path).FullName;

	public void DeleteDirectory(string path)
	{
		if (Directory.Exists(path))
			Directory.Delete(path, recursive: true);
	}

	public void CopyDirectory(string sourceDirectory, string targetDirectory) =>
		CopyDirectory(new DirectoryInfo(sourceDirectory), targetDirectory);

	private static void CopyDirectory(DirectoryInfo source, string targetPath)
	{
		Directory.CreateDirectory(targetPath);
		foreach (FileInfo file in source.EnumerateFiles())
			file.CopyTo(Path.Combine(targetPath, file.Name), overwrite: false);

		foreach (DirectoryInfo child in source.EnumerateDirectories())
			CopyDirectory(child, Path.Combine(targetPath, child.Name));
	}

	public void DeleteFile(string path)
	{
		if (File.Exists(path))
			File.Delete(path);
	}

	public void RenameDirectory(string sourceDirectory, string targetDirectory) =>
		Directory.Move(sourceDirectory, targetDirectory);
}
