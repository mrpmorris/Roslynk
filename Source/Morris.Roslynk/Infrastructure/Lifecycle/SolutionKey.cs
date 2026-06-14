namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// A normalized identity for a solution file, used to share one <see cref="RoslynInstance"/> per
/// solution across sessions. The path is made absolute and compared case-insensitively (Windows).
/// </summary>
public readonly struct SolutionKey : IEquatable<SolutionKey>
{
	public string Path { get; }

	private SolutionKey(string path) => Path = path;

	public static SolutionKey For(string solutionPath)
	{
		if (string.IsNullOrWhiteSpace(solutionPath))
			throw new ArgumentException("A solution path is required.", nameof(solutionPath));

		return new SolutionKey(System.IO.Path.GetFullPath(solutionPath));
	}

	public bool Equals(SolutionKey other) =>
		string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

	public override bool Equals(object? obj) =>
		obj is SolutionKey other && Equals(other);

	public override int GetHashCode() =>
		StringComparer.OrdinalIgnoreCase.GetHashCode(Path);

	public override string ToString() => Path;
}
