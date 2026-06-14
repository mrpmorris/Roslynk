namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// Thrown when a file changed on disk since Roslynk loaded it, so an apply computed against the old
/// content is refused rather than clobbering the newer file.
/// </summary>
public sealed class StaleWriteException : Exception
{
	public string FilePath { get; }

	public StaleWriteException(string filePath)
		: base($"'{filePath}' changed on disk since it was loaded; the edit was not applied.")
	{
		FilePath = filePath;
	}
}
