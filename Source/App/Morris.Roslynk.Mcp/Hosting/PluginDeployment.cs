using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The deployment mechanics behind `roslynk plugin add` and `roslynk plugin remove`: naming and
/// layout validation (no metadata probe — whether the DLL is a plugin at all is discovered at daemon
/// start), the case-collision refusal, and the stage-then-swap that replaces a whole deployment so no
/// stale private dependency survives from the previous version. The daemon is stopped before the swap
/// runs; the delete step can fail on DLLs still locked by the exiting daemon's load contexts, so it
/// is retried within a short window.
/// </summary>
internal static class PluginDeployment
{
	private const string EntryDllSuffix = ".Roslynk.Plugin.dll";
	private const string StagingPrefix = ".staging-";
	private static readonly TimeSpan SwapLockWait = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Resolves `{path}` into the plugin name and the source folder to copy. A file path must name a
	/// DLL ending in .Roslynk.Plugin.dll — the target folder name is the substring before that
	/// suffix, and the whole folder containing the file is copied (the containing folder's own name
	/// is irrelevant, so adding directly from build output works). A folder path must contain
	/// {FolderName}.Roslynk.Plugin.dll directly inside it, and the folder's own name must equal
	/// {FolderName}; the whole folder is copied. Anything else is an error and nothing is deployed.
	/// </summary>
	public static (string Name, string SourceFolder) ValidateAdd(IFileSystem fileSystem, string path)
	{
		ArgumentNullException.ThrowIfNullOrWhiteSpace(path);

		if (fileSystem.FileExists(path))
		{
			string fileName = Path.GetFileName(path);
			if (!fileName.EndsWith(EntryDllSuffix, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException($"'{fileName}' is not a plugin entry assembly; the path must name a DLL ending in '{EntryDllSuffix}'.");

			string name = fileName[..^EntryDllSuffix.Length];
			ValidateName(name);
			string sourceFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
			return (name, sourceFolder);
		}

		if (fileSystem.DirectoryExists(path))
		{
			string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
			string entryPath = Path.Combine(path, $"{folderName}{EntryDllSuffix}");
			if (!fileSystem.FileExists(entryPath))
				throw new ArgumentException($"'{folderName}' contains no {folderName}{EntryDllSuffix} directly inside it; the folder must be the deployment folder itself, named after the plugin.");

			ValidateName(folderName);
			return (folderName, Path.GetFullPath(path));
		}

		throw new ArgumentException($"'{path}' is neither a plugin entry assembly nor a deployment folder.");
	}

	/// <summary>
	/// Refuses to deploy when an existing deployment's normalized name collides: a folder whose name
	/// differs only in case would be skipped at every start as duplicate-name, so the command reports
	/// the collision and deploys nothing. A folder whose name matches exactly is the normal replace
	/// case and proceeds to the swap.
	/// </summary>
	public static void RefuseCaseCollision(IFileSystem fileSystem, string pluginsRoot, string name)
	{
		string normalized = PluginNameRules.Normalize(name);
		foreach (string existing in fileSystem.GetDirectoryNames(pluginsRoot))
		{
			if (!existing.Equals(name, StringComparison.Ordinal)
				&& PluginNameRules.Normalize(existing) == normalized)
			{
				throw new ArgumentException(
					$"'{Path.Combine(pluginsRoot, existing)}' already holds a deployment of this plugin under a name that differs only in case; deploying '{name}' beside it would leave both skipped as duplicate-name at every daemon start. Remove '{existing}' first ('roslynk plugin remove {existing}').");
			}
		}
	}

	/// <summary>
	/// Stages the deployment into a sibling temporary folder named .staging-{Name} — deleting any
	/// .staging-{Name} left by a previous crashed run — then swaps it into {Name}/ by deleting the
	/// existing folder and renaming the staged one into place. A crash during staging leaves the
	/// previous deployment intact; a crash between the delete and the rename can leave the plugin
	/// absent, recovered by running the command again.
	/// </summary>
	public static void Deploy(IFileSystem fileSystem, string pluginsRoot, string name, string sourceFolder)
	{
		string target = Path.Combine(pluginsRoot, name);
		string staging = Path.Combine(pluginsRoot, $"{StagingPrefix}{name}");
		string parent = Path.GetDirectoryName(target)!;
		if (!fileSystem.DirectoryExists(parent))
			fileSystem.CreateDirectory(parent);

		fileSystem.DeleteDirectory(staging);
		fileSystem.CopyDirectory(sourceFolder, staging);

		// The exiting daemon's load contexts can hold the old deployment's DLLs for a moment after
		// the port freed; the delete is retried within a short window before giving up.
		DateTime deadline = DateTime.UtcNow + SwapLockWait;
		while (true)
		{
			try
			{
				fileSystem.DeleteDirectory(target);
				break;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (DateTime.UtcNow >= deadline)
					throw;
				Thread.Sleep(200);
			}
		}

		fileSystem.CopyDirectory(staging, target);
		fileSystem.DeleteDirectory(staging);
	}

	/// <summary>
	/// Resolves the removal target against the plugins root's directories before the daemon is
	/// touched. A name matching no directory — including one that fails the plugin-name regex —
	/// returns null, and the command exits without stopping the daemon. On a case-sensitive
	/// filesystem holding two folders that share a normalized name, the folder exactly matching the
	/// given name is deleted.
	/// </summary>
	public static string? ResolveRemoval(IFileSystem fileSystem, string pluginsRoot, string name)
	{
		ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

		IReadOnlyList<string> directories = fileSystem.GetDirectoryNames(pluginsRoot);
		foreach (string directory in directories)
		{
			if (directory.Equals(name, StringComparison.Ordinal))
				return directory;
		}

		// A name that differs only in case still identifies the deployment on a case-insensitive
		// filesystem; several matches stay unresolved (the exact name decides there).
		string normalized = PluginNameRules.Normalize(name);
		List<string> sameNormalized = directories
			.Where(directory => PluginNameRules.Normalize(directory) == normalized)
			.ToList();
		return sameNormalized.Count == 1 ? sameNormalized[0] : null;
	}

	private static void ValidateName(string name)
	{
		if (!PluginNameRules.IsValid(name))
			throw new ArgumentException($"'{name}' does not match the plugin-name rules ({PluginNameRules.MinLength}-{PluginNameRules.MaxLength} characters, a letter first, then letters or digits); rename the assembly or folder so the plugin, the entry DLL and the deployment folder are all a valid {{Name}}.");
	}
}
