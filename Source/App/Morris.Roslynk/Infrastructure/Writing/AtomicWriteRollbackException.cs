namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// A batch write failed and at least one already-replaced file could not be restored. Each such file's
/// previous content is kept beside it at <c>&lt;path&gt;.roslynk.bak</c>.
/// </summary>
public sealed class AtomicWriteRollbackException : IOException
{
	public IReadOnlyList<string> UnrestoredPaths { get; }

	public AtomicWriteRollbackException(IReadOnlyList<string> unrestoredPaths, Exception commitFailure, IReadOnlyList<Exception> restoreFailures)
		: base(BuildMessage(unrestoredPaths), new AggregateException([commitFailure, .. restoreFailures]))
	{
		UnrestoredPaths = unrestoredPaths;
	}

	private static string BuildMessage(IReadOnlyList<string> paths) =>
		"A batch write failed and could not be fully rolled back. These files keep the new content; their previous content is preserved at "
		+ string.Join("; ", paths.Select(path => $"'{path}{AtomicFileWriter.BackupFileSuffix}' (for '{path}')"))
		+ ". Restore them by hand.";
}
