using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginDiscoveryTests : IDisposable
{
	private readonly string Root;

	public PluginDiscoveryTests()
	{
		Root = Path.Combine(Path.GetTempPath(), "roslynk-discovery-tests-" + Guid.NewGuid().ToString("N"));
		_ = System.IO.Directory.CreateDirectory(Root);
	}

	public void Dispose()
	{
		try
		{
			System.IO.Directory.Delete(Root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void WhenThePluginsRootDoesNotExist_ThenThereAreNoCandidatesAndNoRecords()
	{
		PluginDiscoveryResult result = Discover("missing-root");

		Assert.Empty(result.Candidates);
		Assert.Empty(result.Records);
	}

	[Fact]
	public void WhenAFolderNameIsInvalid_ThenItIsReportedAndSkipped()
	{
		System.IO.Directory.CreateDirectory(Path.Combine(Root, "a"));

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		AssertSingleRecord(result, PluginDiscoveryStage.FolderNameInvalid);
	}

	[Fact]
	public void WhenTheEntryDllIsMissing_ThenItIsReportedAndSkipped()
	{
		System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph"));

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		AssertSingleRecord(result, PluginDiscoveryStage.EntryDllMissing);
	}

	[Fact]
	public void WhenTheEntryBaseNameDoesNotMatchTheFolder_ThenItIsReportedAndSkipped()
	{
		string folder = System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph")).FullName;
		TestPluginCompiler.Emit(Path.Combine(folder, "Wrong.Roslynk.Plugin.dll"));

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		AssertSingleRecord(result, PluginDiscoveryStage.NameMismatch);
	}

	[Fact]
	public void WhenALooseEntryAssemblySitsInTheRoot_ThenTheRecordNamesTheCorrectiveAction()
	{
		TestPluginCompiler.Emit(Path.Combine(Root, "CallGraph.Roslynk.Plugin.dll"));

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		PluginDiscoveryRecord record = AssertSingleRecord(result, PluginDiscoveryStage.NameMismatch);
		Assert.Contains("move it into a folder named 'CallGraph'", record.Reason);
	}

	[Fact]
	public void WhenADuplicateNormalizedNameAppears_ThenTheSecondFolderIsReported()
	{
		string first = System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph")).FullName;
		TestPluginCompiler.Emit(Path.Combine(first, "CallGraph.Roslynk.Plugin.dll"));

		// Windows cannot hold 'CallGraph' and 'callgraph' side by side, so the staged filesystem
		// provides the case-sensitive layout the identity rule exists for.
		PluginDiscoveryResult result = new PluginDiscovery(
			new StagedDirectoryNames(Root, ["CallGraph", "callgraph"]),
			Root,
			ApiVersion.V1,
			ApiVersion.V1)
			.Discover();

		PluginCandidate candidate = Assert.Single(result.Candidates);
		Assert.Equal("CallGraph", candidate.PluginName);
		PluginDiscoveryRecord record = Assert.Single(result.Records, candidate => candidate.Stage == PluginDiscoveryStage.DuplicateName);
		Assert.Equal("callgraph", record.PluginName);
	}

	[Fact]
	public void WhenTheApiVersionIsOutsideTheSupportedRange_ThenItIsReportedAndSkipped()
	{
		string folder = System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph")).FullName;
		TestPluginCompiler.Emit(
			Path.Combine(folder, "CallGraph.Roslynk.Plugin.dll"),
			"""
			[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion((Morris.Roslynk.Plugins.V1.ApiVersion)2)]
			""");

		PluginDiscoveryResult result = Discover();

		Assert.Empty(result.Candidates);
		PluginDiscoveryRecord record = AssertSingleRecord(result, PluginDiscoveryStage.UnsupportedVersion);
		Assert.Equal("2", record.ContractVersion);
	}

	[Fact]
	public void WhenTheCandidateIsValid_ThenItIsListed()
	{
		string folder = System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph")).FullName;
		TestPluginCompiler.Emit(Path.Combine(folder, "CallGraph.Roslynk.Plugin.dll"));

		PluginDiscoveryResult result = Discover();

		PluginCandidate candidate = Assert.Single(result.Candidates);
		Assert.Equal("CallGraph", candidate.PluginName);
		Assert.Equal(folder, candidate.PluginFolderPath);
		Assert.Equal(Path.Combine(folder, "CallGraph.Roslynk.Plugin.dll"), candidate.EntryAssemblyPath);
		Assert.Equal(ApiVersion.V1, candidate.ApiVersion);
		Assert.DoesNotContain(result.Records, record => record.Severity == PluginDiscoverySeverity.Warning);
	}

	[Fact]
	public void WhenAPluginReferencesANewerRoslynThanTheHost_ThenASkewWarningIsRecorded()
	{
		string folder = System.IO.Directory.CreateDirectory(Path.Combine(Root, "CallGraph")).FullName;
		TestPluginCompiler.Emit(
			Path.Combine(folder, "CallGraph.Roslynk.Plugin.dll"),
			"""
			[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion(Morris.Roslynk.Plugins.V1.ApiVersion.V1)]

			internal static class UsesRoslyn
			{
				// Using a Roslyn type is what puts the Microsoft.CodeAnalysis reference row in the PE;
				// an attribute-only source emits none and there would be nothing for the check to see.
				internal static readonly Microsoft.CodeAnalysis.Location? Where;
			}
			""",
			roslynReference: true);

		// A host version of 1.0.0 makes every Microsoft.CodeAnalysis reference in the emitted plugin
		// a forward skew, since the host's real Roslyn is much newer than 1.0.0.
		PluginDiscoveryResult result = Discover(hostRoslynVersion: new Version(1, 0, 0));

		Assert.Single(result.Candidates);
		PluginDiscoveryRecord record = AssertSingleRecord(result, PluginDiscoveryStage.RoslynVersionSkew);
		Assert.Equal(PluginDiscoverySeverity.Warning, record.Severity);
		Assert.Equal("CallGraph", record.PluginName);
	}

	private PluginDiscoveryResult Discover(
		string? rootName = null,
		Version? hostRoslynVersion = null) =>
		new PluginDiscovery(
			DefaultFileSystem.Instance,
			rootName is null ? Root : Path.Combine(Root, rootName),
			ApiVersion.V1,
			ApiVersion.V1,
			hostRoslynVersion: hostRoslynVersion)
			.Discover();

	private PluginDiscoveryRecord AssertSingleRecord(PluginDiscoveryResult result, PluginDiscoveryStage stage)
	{
		PluginDiscoveryRecord? record = Assert.Single(result.Records, candidate => candidate.Stage == stage);
		return record;
	}
}

/// <summary>Stages directory names the real filesystem cannot provide (case-variant duplicates).</summary>
internal sealed class StagedDirectoryNames(string root, string[] names) : IFileSystem
{
	private readonly IFileSystem Inner = DefaultFileSystem.Instance;

	public bool DirectoryExists(string path) => Inner.DirectoryExists(path);

	public IReadOnlyList<string> GetDirectoryNames(string path) =>
		string.Equals(path, root, StringComparison.Ordinal)
			? names.OrderBy(name => name, StringComparer.Ordinal).ToArray()
			: Inner.GetDirectoryNames(path);

	public bool FileExists(string path) => Inner.FileExists(path);

	public IReadOnlyList<string> GetFileNames(string path) => Inner.GetFileNames(path);
}
