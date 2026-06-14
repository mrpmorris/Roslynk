namespace Morris.Roslynk.Features.Patching.ApplyPatch;

/// <summary>
/// The version a patched file was based on: the path as it appears in the patch header (or its absolute
/// path) and the <c>documentVersion</c> hash from when it was read. The apply re-checks the file on disk
/// against this hash and refuses if it moved.
/// </summary>
public sealed class FileVersion
{
	public string Path { get; }
	public string Version { get; }

	public FileVersion(string path, string version)
	{
		Path = path;
		Version = version;
	}
}
