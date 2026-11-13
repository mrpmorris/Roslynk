using Microsoft.Extensions.Logging;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginDiscoveryTests : IDisposable
{
	private readonly string Root;

	public PluginDiscoveryTests()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslynk-discovery-tests-" + Guid.NewGuid().ToString("N"));
		_ = Directory.CreateDirectory(Root);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private PluginDiscoveryResult Discover()
	{
		var logger = new CapturingLogger<PluginDiscovery>();
		PluginDiscoveryResult result = new PluginDiscovery(new DefaultFileSystem(), Root, logger).Discover();
		Assert.Equal(result.Records.Count, logger.Records.Count(record => record.Level == LogLevel.Error));
		return result;
	}

	private void Stage(string folderName, string? entryDllName = null)
	{
		string folder = Path.Combine(Root, folderName);
		Directory.CreateDirectory(folder);
		if (entryDllName is not null)
			File.WriteAllText(Path.Combine(folder, entryDllName), "not a real assembly - discovery reads names only");
	}

	[Fact]
	public void WhenThePluginsRootDoesNotExist_ThenThereAreNoCandidatesAndNoRecords()
	{
		PluginDiscoveryResult result = new PluginDiscovery(new DefaultFileSystem(), Path.Combine(Root, "missing"), new CapturingLogger<PluginDiscovery>()).Discover();

		Assert.Empty(result.Candidates);
		Assert.Empty(result.Records);
	}

	[Fact]
	public void WhenAFolderNameIsInvalid_ThenItIsReportedAsFolderNameInvalidAndSkipped()
	{
		Stage("a");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		PluginFailureStage? stage = Assert.Single(result.Records).Stage;
		Assert.Equal(PluginFailureStage.FolderNameInvalid, stage);
	}

	[Fact]
	public void WhenTheEntryDllIsMissing_ThenItIsReportedAsEntryDllMissingAndSkipped()
	{
		Stage("CallGraph");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		var record = Assert.Single(result.Records);
		Assert.Equal(PluginFailureStage.EntryDllMissing, record.Stage);
		Assert.Contains("CallGraph", record.Reason);
	}

	[Fact]
	public void WhenTheEntryBaseNameDoesNotMatchTheFolder_ThenItIsReportedAsNameMismatchNamingTheDll()
	{
		Stage("CallGraph", "WrongName.Roslynk.Plugin.dll");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		var record = Assert.Single(result.Records);
		Assert.Equal(PluginFailureStage.NameMismatch, record.Stage);
		Assert.Contains("WrongName.Roslynk.Plugin.dll", record.Reason);
		Assert.Contains("rename the DLL", record.Reason);
	}

	[Fact]
	public void WhenAnEntryDllSitsLooseInTheRoot_ThenItIsRecordedStageLessWithItsOwnCorrectiveAction()
	{
		File.WriteAllText(Path.Combine(Root, "CallGraph.Roslynk.Plugin.dll"), "not a real assembly");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		var record = Assert.Single(result.Records);
		Assert.Null(record.Stage);
		Assert.Contains("move it into a folder named 'CallGraph'", record.Reason);
	}

	[Fact]
	public void WhenTwoFoldersShareANormalizedName_ThenTheLaterOneIsReportedAsDuplicateNameAndTheEarlierWins()
	{
		// NTFS cannot stage two case-variant siblings (the second CreateDirectory returns the
		// first), so the collision is staged through the filesystem seam.
		var fileSystem = new MemoryFileSystem();
		fileSystem.StageFolder(Root, "CallGraph", "CallGraph.Roslynk.Plugin.dll");
		fileSystem.StageFolder(Root, "callgraph", "callgraph.Roslynk.Plugin.dll");

		var logger = new CapturingLogger<PluginDiscovery>();
		PluginDiscoveryResult result = new PluginDiscovery(fileSystem, Root, logger).Discover();
		Assert.Equal(result.Records.Count, logger.Records.Count(record => record.Level == LogLevel.Error));

		Assert.Single(result.Candidates);
		Assert.Equal("CallGraph", result.Candidates[0].PluginName);
		var record = Assert.Single(result.Records);
		Assert.Equal(PluginFailureStage.DuplicateName, record.Stage);
		Assert.Contains(Path.Combine(Root, "CallGraph"), record.Reason);
		Assert.Contains(Path.Combine(Root, "callgraph"), record.Reason);
		Assert.Contains("ordinal examination order", record.Reason);
	}

	[Fact]
	public void WhenAStagingFolderForAValidNameExists_ThenItIsDeletedSilently()
	{
		string staging = Path.Combine(Root, ".staging-CallGraph");
		Directory.CreateDirectory(staging);
		File.WriteAllText(Path.Combine(staging, "leftover.dll"), "staged debris");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		Assert.Empty(result.Records);
		Assert.False(Directory.Exists(staging));
	}

	[Fact]
	public void WhenAStagingFolderHasAnInvalidName_ThenItIsReportedAsFolderNameInvalidAndKept()
	{
		string staging = Path.Combine(Root, ".staging-x");
		Directory.CreateDirectory(staging);

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		var record = Assert.Single(result.Records);
		Assert.Equal(PluginFailureStage.FolderNameInvalid, record.Stage);
		Assert.True(Directory.Exists(staging));
	}

	[Fact]
	public void WhenFoldersAreExamined_ThenTheyAreExaminedInOrdinalOrderAndCandidatesKeepIt()
	{
		Stage("Beta", "Beta.Roslynk.Plugin.dll");
		Stage("alpha", "alpha.Roslynk.Plugin.dll");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Records);
		Assert.Equal(2, result.Candidates.Count);
		Assert.Equal("Beta", result.Candidates[0].PluginName);
		Assert.Equal("alpha", result.Candidates[1].PluginName);
	}

	[Fact]
	public void WhenTheEntryDllExists_ThenTheCandidateCarriesTheFolderPathAndEntryPath()
	{
		Stage("CallGraph", "CallGraph.Roslynk.Plugin.dll");

		PluginDiscoveryResult result = Discover();

		var candidate = Assert.Single(result.Candidates);
		Assert.Equal("CallGraph", candidate.PluginName);
		Assert.Equal(Path.Combine(Root, "CallGraph"), candidate.PluginFolderPath);
		Assert.Equal(Path.Combine(Root, "CallGraph", "CallGraph.Roslynk.Plugin.dll"), candidate.EntryAssemblyPath);
	}
}
