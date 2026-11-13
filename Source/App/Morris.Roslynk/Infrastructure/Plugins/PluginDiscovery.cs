namespace Morris.Roslynk.Infrastructure.Plugins;
using Microsoft.Extensions.Logging;

/// <summary>
/// Walks the plugins root once: deletes crashed-deployment staging debris, classifies every folder
/// against the naming rules, and reports every rejection as a record. A folder that fails any stage
/// is skipped — nothing valid is deleted or moved; the next start re-examines it. There is no
/// pre-load probe: nothing about a candidate's contents is read here — loading happens in
/// <see cref="PluginLoader"/>, which is the point of record for everything but naming and layout.
/// </summary>
public sealed class PluginDiscovery
{
	private const string EntryDllSuffix = ".Roslynk.Plugin.dll";
	private const string StagingPrefix = ".staging-";

	private readonly IFileSystem FileSystem;
	private readonly string PluginsRoot;
	private readonly ILogger<PluginDiscovery> Logger;

	public PluginDiscovery(IFileSystem fileSystem, string pluginsRoot, ILogger<PluginDiscovery> logger)
	{
		FileSystem = fileSystem;
		PluginsRoot = pluginsRoot;
		Logger = logger;
	}

	/// <summary>The discovery that walks the user's own plugins root, '~/.roslynk/plugins'.</summary>
	internal static PluginDiscovery ForUserPluginsRoot(IFileSystem fileSystem, string? pluginsRoot, ILogger<PluginDiscovery> logger) =>
		new(
			fileSystem,
			pluginsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".roslynk", "plugins"),
			logger);

	public PluginDiscoveryResult Discover()
	{
		var records = new List<PluginDiscoveryRecord>();
		var candidates = new List<PluginCandidate>();

		if (!FileSystem.DirectoryExists(PluginsRoot))
			return new PluginDiscoveryResult(candidates, records);

		RecordLooseEntryAssemblies(records);
		DiscoverFolders(records, candidates);

		foreach (PluginDiscoveryRecord record in records)
			Logger.LogError(
				"Roslynk plugin discovery rejected a candidate: {Reason} [stage={Stage}] plugin={Plugin} folder={Folder}",
				record.Reason,
				record.Stage?.ToStageToken() ?? "(none)",
				record.PluginName ?? "(unknown)",
				record.PluginFolder ?? "(unknown)");

		return new PluginDiscoveryResult(candidates, records);
	}

	/// <summary>
	/// An entry-DLL-shaped file loose in the plugins root is recorded stage-less — "move it into a
	/// folder named after its base name" — and skipped.
	/// </summary>
	private void RecordLooseEntryAssemblies(List<PluginDiscoveryRecord> records)
	{
		foreach (string fileName in FileSystem.GetFileNames(PluginsRoot))
		{
			if (!fileName.EndsWith(EntryDllSuffix, StringComparison.OrdinalIgnoreCase))
				continue;

			string baseName = fileName[..^EntryDllSuffix.Length];
			records.Add(PluginDiscoveryRecord.Create(
				null,
				$"A loose entry assembly '{fileName}' sits in the plugins root; move it into a folder named '{baseName}'",
				pluginName: PluginNameRules.IsValid(baseName) ? baseName : null));
		}
	}

	private void DiscoverFolders(List<PluginDiscoveryRecord> records, List<PluginCandidate> candidates)
	{
		var takenNames = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (string folderName in FileSystem.GetDirectoryNames(PluginsRoot))
		{
			if (folderName.StartsWith(StagingPrefix, StringComparison.Ordinal))
			{
				ClassifyStagingDebris(records, folderName);
				continue;
			}

			if (!PluginNameRules.IsValid(folderName))
			{
				records.Add(PluginDiscoveryRecord.Create(
					PluginFailureStage.FolderNameInvalid,
					$"'{Path.Combine(PluginsRoot, folderName)}' does not match the plugin-name rules ({PluginNameRules.MinLength}-{PluginNameRules.MaxLength} characters, a letter first, then letters or digits); rename the folder to the plugin's {nameof(PluginNameRules)}-valid name",
					pluginFolder: folderName));
				continue;
			}

			string folderPath = Path.Combine(PluginsRoot, folderName);
			string entryPath = Path.Combine(folderPath, $"{folderName}{EntryDllSuffix}");
			if (!TryClassifyEntry(records, folderName, folderPath, entryPath))
				continue;

			string normalized = PluginNameRules.Normalize(folderName);
			if (takenNames.TryGetValue(normalized, out string? firstFolder))
			{
				records.Add(PluginDiscoveryRecord.Create(
					PluginFailureStage.DuplicateName,
					$"'{Path.Combine(PluginsRoot, folderName)}' normalizes to '{normalized}', already reserved by '{Path.Combine(PluginsRoot, firstFolder)}'; ordinal examination order selected the earlier folder as the winner, so this one is skipped",
					pluginName: folderName,
					pluginFolder: folderName));
				continue;
			}

			takenNames[normalized] = folderName;
			candidates.Add(new PluginCandidate(folderName, folderPath, entryPath));
		}
	}

	/// <summary>
	/// A folder named .staging-{Name} for a regex-valid {Name} is debris from a crashed plugin add
	/// staging step: it is deleted, and no record is produced. Any other .staging- name is reported
	/// as folder-name-invalid.
	/// </summary>
	private void ClassifyStagingDebris(List<PluginDiscoveryRecord> records, string folderName)
	{
		string candidateName = folderName[StagingPrefix.Length..];
		if (!PluginNameRules.IsValid(candidateName))
		{
			records.Add(PluginDiscoveryRecord.Create(
				PluginFailureStage.FolderNameInvalid,
				$"'{Path.Combine(PluginsRoot, folderName)}' is not a valid plugin deployment or staging folder ({StagingPrefix}{{Name}} with a valid {{Name}}); remove it",
				pluginFolder: folderName));
			return;
		}

		FileSystem.DeleteDirectory(Path.Combine(PluginsRoot, folderName));
	}

	/// <returns>Whether the folder holds a matching entry assembly that may proceed to the name check.</returns>
	private bool TryClassifyEntry(List<PluginDiscoveryRecord> records, string folderName, string folderPath, string entryPath)
	{
		if (FileSystem.FileExists(entryPath))
			return true;

		string? misnamedEntry = FileSystem.GetFileNames(folderPath)
			.FirstOrDefault(fileName => fileName.EndsWith(EntryDllSuffix, StringComparison.OrdinalIgnoreCase));
		records.Add(PluginDiscoveryRecord.Create(
			misnamedEntry is null ? PluginFailureStage.EntryDllMissing : PluginFailureStage.NameMismatch,
			misnamedEntry is null
				? $"Folder '{folderPath}' contains no {folderName}{EntryDllSuffix}"
				: $"Folder '{folderPath}' contains the entry assembly '{misnamedEntry}', whose base name does not match the folder name; rename the DLL or fix the project's AssemblyName so the assembly, the folder and the plugin are all '{folderName}'",
			pluginName: folderName,
			pluginFolder: folderName));
		return false;
	}
}
