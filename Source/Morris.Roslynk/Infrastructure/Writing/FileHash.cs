using System.Security.Cryptography;
using System.Text;

namespace Morris.Roslynk.Infrastructure.Writing;

/// <summary>
/// The single definition of a file's version: the SHA256 of its bytes, hex-encoded. The hash is the
/// source of truth for staleness (it changes by itself whenever anyone edits the file), so reads report
/// it as <c>documentVersion</c> and writes re-check it before committing.
/// </summary>
public static class FileHash
{
	public static string Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

	/// <summary>The version of <paramref name="text"/> as it would be once written as UTF-8.</summary>
	public static string Of(string text) => Of(Encoding.UTF8.GetBytes(text));

	/// <summary>The version of the file on disk, or null if it is missing or cannot be read.</summary>
	public static async Task<string?> TryOfFileAsync(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			return File.Exists(path)
				? Of(await File.ReadAllBytesAsync(path, cancellationToken))
				: null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}
}
