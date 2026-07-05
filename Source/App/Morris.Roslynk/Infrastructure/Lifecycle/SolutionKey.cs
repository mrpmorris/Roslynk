namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// A normalized identity for a solution file, used to share one <see cref="RoslynInstance"/> per
/// solution across sessions. The path is made absolute; comparison follows the platform's default
/// filesystem convention (case-insensitive on Windows and macOS, case-sensitive elsewhere).
/// Case-insensitive mounts on Linux (e.g. WSL's /mnt/c) are still compared case-sensitively, so
/// clients there must use a consistent casing per solution.
/// </summary>
public readonly struct SolutionKey : IEquatable<SolutionKey>
{
	public string FilePath { get; }

	private static StringComparer PathComparer =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;

	private SolutionKey(string filePath) => FilePath = filePath;

	public static SolutionKey For(string solutionPath)
	{
		if (string.IsNullOrWhiteSpace(solutionPath))
			throw new ArgumentException("A solution path is required.", nameof(solutionPath));

		return new SolutionKey(System.IO.Path.GetFullPath(solutionPath));
	}

	public bool Equals(SolutionKey other) => PathComparer.Equals(FilePath, other.FilePath);

	public override bool Equals(object? obj) =>
		obj is SolutionKey other && Equals(other);

	public override int GetHashCode() => PathComparer.GetHashCode(FilePath);

	public override string ToString() => FilePath;
}
