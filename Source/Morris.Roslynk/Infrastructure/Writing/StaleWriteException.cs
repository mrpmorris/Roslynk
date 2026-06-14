namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Thrown when a file changed on disk since Roslynk loaded it, so an apply computed against the old
/// content is refused rather than clobbering the newer file.
/// </summary>
public sealed class StaleWriteException : Exception
{
	public string Path { get; }

	public StaleWriteException(string path)
		: base($"'{path}' changed on disk since it was loaded; the edit was not applied.")
	{
		Path = path;
	}
}
