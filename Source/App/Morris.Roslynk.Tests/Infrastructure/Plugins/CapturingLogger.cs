using Microsoft.Extensions.Logging;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>Captures the plugin pipeline's OpenTelemetry-shaped log records for assertions.</summary>
public sealed class CapturingLogger : ILogger
{
	public List<(LogLevel Level, string Message, Exception? Exception)> Records { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (logLevel >= LogLevel.Information)
			Records.Add((logLevel, formatter(state, exception), exception));
	}
}

public sealed class CapturingLogger<TCategory> : ILogger<TCategory>
{
	private readonly CapturingLogger Inner = new();

	public List<(LogLevel Level, string Message, Exception? Exception)> Records => Inner.Records;

	IDisposable? ILogger.BeginScope<TState>(TState state) => Inner.BeginScope(state);

	bool ILogger.IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

	void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
		Inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Collects the tools the loader registers, so rollback and duplicate paths can be asserted.</summary>
public sealed class ListPluginToolSink : IPluginToolSink
{
	public List<ModelContextProtocol.Server.McpServerTool> Added { get; } = [];

	public void Add(ModelContextProtocol.Server.McpServerTool tool) => Added.Add(tool);

	public void Remove(ModelContextProtocol.Server.McpServerTool tool) => Added.Remove(tool);
}

/// <summary>
/// An in-memory <see cref="IFileSystem"/>, staged by path. NTFS cannot hold two case-variant
/// siblings (the second CreateDirectory returns the first), so duplicate-name layouts are staged
/// here instead of on disk.
/// </summary>
public sealed class MemoryFileSystem : IFileSystem
{
	private readonly SortedSet<string> Directories = new(StringComparer.Ordinal);
	private readonly SortedSet<string> Files = new(StringComparer.Ordinal);

	public void StageFolder(string parent, string name, string fileName)
	{
		_ = CreateDirectory(Path.Combine(parent, name));
		Files.Add(Normalize(Path.Combine(parent, name, fileName)));
	}

	private static string Normalize(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

	public bool DirectoryExists(string path) =>
		Directories.Contains(Normalize(path));

	public IReadOnlyList<string> GetDirectoryNames(string path)
	{
		string normalized = Normalize(path) + Path.DirectorySeparatorChar;
		return Directories
			.Where(directory => directory.StartsWith(normalized, StringComparison.Ordinal)
				&& !directory[normalized.Length..].Contains(Path.DirectorySeparatorChar))
			.Select(directory => directory[normalized.Length..])
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
	}

	public bool FileExists(string path) =>
		Files.Contains(Normalize(path));

	public IReadOnlyList<string> GetFileNames(string path)
	{
		string normalized = Normalize(path) + Path.DirectorySeparatorChar;
		return Files
			.Where(file => file.StartsWith(normalized, StringComparison.Ordinal)
				&& !file[normalized.Length..].Contains(Path.DirectorySeparatorChar))
			.Select(file => file[normalized.Length..])
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
	}

	public string CreateDirectory(string path)
	{
		string normalized = Normalize(path);
		string? current = Path.GetDirectoryName(normalized);
		while (current is not null)
		{
			Directories.Add(current);
			current = Path.GetDirectoryName(current);
		}

		Directories.Add(normalized);
		return normalized;
	}

	public void DeleteDirectory(string path) =>
		Directories.Remove(Normalize(path));

	public void CopyDirectory(string sourceDirectory, string targetDirectory)
	{
		string source = Normalize(sourceDirectory) + Path.DirectorySeparatorChar;
		CreateDirectory(targetDirectory);
		string target = Normalize(targetDirectory) + Path.DirectorySeparatorChar;
		foreach (string file in Files.Where(file => file.StartsWith(source, StringComparison.Ordinal)))
			Files.Add(target + file[source.Length..]);
	}

	public void DeleteFile(string path) =>
		Files.Remove(Normalize(path));

	public void RenameDirectory(string sourceDirectory, string targetDirectory)
	{
		string source = Normalize(sourceDirectory);
		string target = Normalize(targetDirectory);
		if (!Directories.Contains(source))
			throw new DirectoryNotFoundException(source);

		if (Directories.Contains(target) || Files.Any(file => file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
			throw new IOException($"'{target}' already exists.");

		string sourcePrefix = source + Path.DirectorySeparatorChar;
		var movedDirectories = Directories
			.Where(directory => directory == source || directory.StartsWith(sourcePrefix, StringComparison.Ordinal))
			.Select(directory => (from: directory, to: target + directory[source.Length..]))
			.ToList();
		var movedFiles = Files
			.Where(file => file.StartsWith(sourcePrefix, StringComparison.Ordinal))
			.Select(file => (from: file, to: target + file[source.Length..]))
			.ToList();

		foreach ((string from, string to) in movedDirectories)
		{
			Directories.Remove(from);
			Directories.Add(to);
		}

		foreach ((string from, string to) in movedFiles)
		{
			Files.Remove(from);
			Files.Add(to);
		}
	}
}
