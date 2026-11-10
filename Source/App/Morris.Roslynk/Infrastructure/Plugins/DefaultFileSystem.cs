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
}
