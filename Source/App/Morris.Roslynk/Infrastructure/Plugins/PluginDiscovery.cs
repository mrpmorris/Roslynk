using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Walks the plugins root once: classifies every folder against the naming rules, probes each
/// candidate entry assembly, and reports every rejection as a record. A folder that fails any stage
/// is skipped — nothing is deleted or moved; the next start re-examines it.
/// </summary>
public sealed class PluginDiscovery
{
	private const string EntryDllSuffix = ".Roslynk.Plugin.dll";
	private const string RoslynAssemblyPrefix = "Microsoft.CodeAnalysis";

	private readonly IFileSystem FileSystem;
	private readonly string PluginsRoot;
	private readonly ApiVersion MinSupportedVersion;
	private readonly ApiVersion MaxSupportedVersion;
	private readonly string? RuntimeDirectory;
	private readonly Version? HostRoslynVersion;

	public PluginDiscovery(
		IFileSystem fileSystem,
		string pluginsRoot,
		ApiVersion minSupportedVersion,
		ApiVersion maxSupportedVersion,
		string? runtimeDirectory = null,
		Version? hostRoslynVersion = null)
	{
		FileSystem = fileSystem;
		PluginsRoot = pluginsRoot;
		MinSupportedVersion = minSupportedVersion;
		MaxSupportedVersion = maxSupportedVersion;
		RuntimeDirectory = runtimeDirectory;
		HostRoslynVersion = hostRoslynVersion ?? typeof(Microsoft.CodeAnalysis.Solution).Assembly.GetName().Version;
	}

	public PluginDiscoveryResult Discover()
	{
		var records = new List<PluginDiscoveryRecord>();
		var candidates = new List<PluginCandidate>();

		if (!FileSystem.DirectoryExists(PluginsRoot))
			return new PluginDiscoveryResult(candidates, records);

		RecordLooseEntryAssemblies(records);
		DiscoverFolders(records, candidates);

		return new PluginDiscoveryResult(candidates, records);
	}

	private void RecordLooseEntryAssemblies(List<PluginDiscoveryRecord> records)
	{
		foreach (string fileName in FileSystem.GetFileNames(PluginsRoot))
		{
			if (!fileName.EndsWith(EntryDllSuffix, StringComparison.OrdinalIgnoreCase))
				continue;

			string baseName = fileName[..^EntryDllSuffix.Length];
			records.Add(new(
				PluginDiscoveryStage.NameMismatch,
				PluginDiscoverySeverity.Error,
				Clean($"A loose entry assembly '{fileName}' sits in the plugins root; move it into a folder named '{baseName}'"),
				PluginName: PluginNameRules.IsValid(baseName) ? baseName : null,
				PluginFolder: null));
		}
	}

	private void DiscoverFolders(List<PluginDiscoveryRecord> records, List<PluginCandidate> candidates)
	{
		var takenNames = new Dictionary<string, string>(StringComparer.Ordinal);
		var prober = new PluginProber(RuntimeDirectory);

		foreach (string folderName in FileSystem.GetDirectoryNames(PluginsRoot))
		{
			if (!PluginNameRules.IsValid(folderName))
			{
				records.Add(new(
					PluginDiscoveryStage.FolderNameInvalid,
					PluginDiscoverySeverity.Error,
					Clean($"'{folderName}' does not match the plugin-name rules ({PluginNameRules.MinLength}-{PluginNameRules.MaxLength} characters, letters first)"),
					PluginFolder: folderName));
				continue;
			}

			string folderPath = Path.Combine(PluginsRoot, folderName);
			string entryPath = Path.Combine(folderPath, $"{folderName}{EntryDllSuffix}");
			if (!TryClassifyEntry(records, folderName, folderPath, entryPath))
				continue;

			string normalized = PluginNameRules.Normalize(folderName);
			if (takenNames.TryGetValue(normalized, out string? firstFolder))
			{
				records.Add(new(
					PluginDiscoveryStage.DuplicateName,
					PluginDiscoverySeverity.Error,
					Clean($"'{folderName}' normalizes to '{normalized}', already reserved by '{firstFolder}'"),
					PluginName: folderName,
					PluginFolder: folderName));
				continue;
			}

			takenNames[normalized] = folderName;
			ProbeCandidate(prober, records, candidates, folderName, folderPath, entryPath);
		}
	}

	/// <returns>Whether the folder holds a matching entry assembly that may proceed to the name check.</returns>
	private bool TryClassifyEntry(List<PluginDiscoveryRecord> records, string folderName, string folderPath, string entryPath)
	{
		if (FileSystem.FileExists(entryPath))
			return true;

		bool hasOtherEntry = FileSystem.GetFileNames(folderPath).Any(fileName => fileName.EndsWith(EntryDllSuffix, StringComparison.OrdinalIgnoreCase));
		records.Add(new(
			hasOtherEntry ? PluginDiscoveryStage.NameMismatch : PluginDiscoveryStage.EntryDllMissing,
			PluginDiscoverySeverity.Error,
			hasOtherEntry
				? Clean($"Folder '{folderName}' contains an entry assembly whose base name does not match the folder name")
				: Clean($"Folder '{folderName}' contains no {folderName}{EntryDllSuffix}"),
			PluginName: folderName,
			PluginFolder: folderName));
		return false;
	}

	private void ProbeCandidate(
		PluginProber prober,
		List<PluginDiscoveryRecord> records,
		List<PluginCandidate> candidates,
		string folderName,
		string folderPath,
		string entryPath)
	{
		PluginProbeResult probe = prober.Probe(entryPath);
		if (!probe.IsSuccess)
		{
			records.Add(new(
				probe.FailureStage,
				PluginDiscoverySeverity.Error,
				Clean($"{folderName}: {probe.Reason}"),
				PluginName: folderName,
				PluginFolder: folderName));
			return;
		}

		ApiVersion apiVersion = probe.ApiVersion!.Value;
		if (apiVersion < MinSupportedVersion || apiVersion > MaxSupportedVersion)
		{
			records.Add(new(
				PluginDiscoveryStage.UnsupportedVersion,
				PluginDiscoverySeverity.Error,
				Clean($"{folderName} requires API version {(int)apiVersion}; this Roslynk supports {(int)MinSupportedVersion}-{(int)MaxSupportedVersion}"),
				PluginName: folderName,
				PluginFolder: folderName,
				ContractVersion: ((int)apiVersion).ToString()));
			return;
		}

		RecordRoslynVersionSkew(records, folderName, probe);
		candidates.Add(new PluginCandidate(folderName, folderPath, entryPath, apiVersion));
	}

	private void RecordRoslynVersionSkew(List<PluginDiscoveryRecord> records, string folderName, PluginProbeResult probe)
	{
		if (HostRoslynVersion is null)
			return;

		foreach (PluginAssemblyReference reference in probe.ReferencedAssemblies)
		{
			if (!reference.Name.StartsWith(RoslynAssemblyPrefix, StringComparison.Ordinal))
				continue;
			if (!Version.TryParse(reference.Version, out Version? referencedVersion))
				continue;
			if (referencedVersion <= HostRoslynVersion)
				continue;

			// An older reference binds forward to the host's Roslyn and is safe to load; only a
			// newer one may demand types or members the host's Roslyn does not have.
			records.Add(new(
				PluginDiscoveryStage.RoslynVersionSkew,
				PluginDiscoverySeverity.Warning,
				Clean($"{folderName} references {reference.Name} {reference.Version} but this Roslynk runs {HostRoslynVersion}; it may fail to load"),
				PluginName: folderName,
				PluginFolder: folderName));
		}
	}

	private static string Clean(string text) =>
		text.ReplaceLineEndings(" ");
}
