using System.Text.Json;
using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces;

public class RestoreLogsTests
{
	private const string Vulnerability = "Package 'System.Text.Json' 8.0.4 has a known high severity vulnerability, https://github.com/advisories/GHSA-8g4q-xg66-9fp4";
	private const string PruneNotice = "PackageReference System.Text.Json will not be pruned.";

	[Fact]
	public void WhenProjectsHaveWarningEntries_ThenEachIsCollectedWithItsCode()
	{
		using var scratch = new Scratch();
		string first = scratch.Project("First");
		string second = scratch.Project("Second");
		scratch.WriteAssets(first, Warning("NU1903", Vulnerability));
		scratch.WriteAssets(second, Warning("NU1510", PruneNotice));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([first, second], scratch.Root);

		Assert.Equal("NU1903", warnings[Vulnerability]);
		Assert.Equal("NU1510", warnings[PruneNotice]);
		Assert.Equal(2, warnings.Count);
	}

	[Fact]
	public void WhenAnEntryIsInfoOrError_ThenItIsIgnored()
	{
		using var scratch = new Scratch();
		string project = scratch.Project("App");
		scratch.WriteAssets(
			project,
			("NU1903", "Warning", Vulnerability),
			("NU1701", "Info", "Package restored using a different framework."),
			("NU1101", "Error", "Unable to find package."));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([project], scratch.Root);

		Assert.Equal((Vulnerability, "NU1903"), SingleWarning(warnings));
	}

	[Fact]
	public void WhenTwoProjectsShareAWarningMessage_ThenItIsCollectedOnceWithTheFirstCode()
	{
		using var scratch = new Scratch();
		string first = scratch.Project("First");
		string second = scratch.Project("Second");
		scratch.WriteAssets(first, Warning("NU1903", Vulnerability));
		scratch.WriteAssets(second, Warning("NU9999", Vulnerability));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([first, second], scratch.Root);

		Assert.Equal((Vulnerability, "NU1903"), SingleWarning(warnings));
	}

	[Fact]
	public void WhenAnAssetsFileIsMissingOrMalformed_ThenItIsSkippedAndTheOthersAreStillRead()
	{
		using var scratch = new Scratch();
		string missing = scratch.Project("Missing");
		string malformed = scratch.Project("Malformed");
		string readable = scratch.Project("Readable");
		scratch.WriteRawAssets(malformed, "{ \"logs\": [ { \"code\": ");
		scratch.WriteAssets(readable, Warning("NU1903", Vulnerability));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([missing, malformed, readable], scratch.Root);

		Assert.Equal((Vulnerability, "NU1903"), SingleWarning(warnings));
	}

	[Fact]
	public void WhenNoProjectHasTheDefaultIntermediatePath_ThenTheSolutionDirectoryIsScanned()
	{
		// An artifacts layout puts the intermediate output somewhere other than <project>/obj.
		using var scratch = new Scratch();
		string project = scratch.Project("App");
		scratch.WriteAssetsAt(Path.Combine("artifacts", "obj", "App"), Warning("NU1903", Vulnerability));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([project], scratch.Root);

		Assert.Equal((Vulnerability, "NU1903"), SingleWarning(warnings));
	}

	[Fact]
	public void WhenTheSolutionDirectoryIsScanned_ThenBuildOutputFoldersAreSkipped()
	{
		using var scratch = new Scratch();
		string project = scratch.Project("App");
		scratch.WriteAssetsAt(Path.Combine("bin", "Debug"), Warning("NU1903", "A warning inside bin."));
		scratch.WriteAssetsAt(Path.Combine("artifacts", "obj", "App"), Warning("NU1510", PruneNotice));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([project], scratch.Root);

		Assert.Equal((PruneNotice, "NU1510"), SingleWarning(warnings));
	}

	[Fact]
	public void WhenTheScanIsDeeperThanTheLimit_ThenTheFileIsNotFound()
	{
		using var scratch = new Scratch();
		string project = scratch.Project("App");
		string tooDeep = Path.Combine(["a", "b", "c", "d", "e", "f", "g", "h", "i"]);
		scratch.WriteAssetsAt(tooDeep, Warning("NU1903", Vulnerability));

		IReadOnlyDictionary<string, string> warnings = RestoreLogs.ReadWarnings([project], scratch.Root);

		Assert.Empty(warnings);
	}

	[Fact]
	public void WhenAFailureMessageEndsInAWarningText_ThenItIsClassifiedAsThatWarning()
	{
		var warnings = WarningsOf(Vulnerability, "NU1903");
		string message = $"Msbuild failed when processing the file 'C:/src/App/App.csproj' with message: {Vulnerability}";

		Assert.Equal($"NU1903: {Vulnerability}", RestoreLogs.Classify(message, warnings));
	}

	[Fact]
	public void WhenAFailureMessageDoesNotEndInAWarningText_ThenItIsNotClassified()
	{
		var warnings = WarningsOf(Vulnerability, "NU1903");
		string message = "Msbuild failed when processing the file 'C:/src/App/App.csproj' with message: The SDK 'Microsoft.NET.Sdk.Web' specified could not be found.";

		Assert.Null(RestoreLogs.Classify(message, warnings));
	}

	[Fact]
	public void WhenAMessageHasNoFailureMarker_ThenItIsNotClassified()
	{
		var warnings = WarningsOf(Vulnerability, "NU1903");

		Assert.Null(RestoreLogs.Classify(Vulnerability, warnings));
	}

	[Fact]
	public void WhenAMessageDiffersOnlyInCase_ThenItIsNotClassified()
	{
		// The match is ordinal: the assets file and the workspace report the same text, and a different
		// spelling is a different message, which stays a diagnostic.
		var warnings = WarningsOf(Vulnerability, "NU1903");
		string message = $"Msbuild failed when processing the file 'C:/src/App/App.csproj' with message: {Vulnerability.ToUpperInvariant()}";

		Assert.Null(RestoreLogs.Classify(message, warnings));
	}

	[Fact]
	public void WhenWorkspaceFailuresAreSplit_ThenWarningsAndFailuresKeepTheirOrder()
	{
		var warnings = WarningsOf(Vulnerability, "NU1903");
		string failure = "Msbuild failed when processing the file 'C:/src/App/App.csproj' with message: The project file could not be loaded.";
		string warning = $"Msbuild failed when processing the file 'C:/src/App/App.csproj' with message: {Vulnerability}";

		(IReadOnlyList<string> matched, IReadOnlyList<string> remaining) = RestoreLogs.Split(
			[
				new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, failure),
				new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, warning),
			],
			warnings);

		Assert.Equal(new[] { failure }, remaining);
		Assert.Equal(new[] { $"NU1903: {Vulnerability}" }, matched);
	}

	[Fact]
	public void WhenTwoProjectsReportTheSameWarning_ThenItIsListedOnce()
	{
		var warnings = WarningsOf(Vulnerability, "NU1903");
		string first = $"Msbuild failed when processing the file 'C:/src/First/First.csproj' with message: {Vulnerability}";
		string second = $"Msbuild failed when processing the file 'C:/src/Second/Second.csproj' with message: {Vulnerability}";

		(IReadOnlyList<string> matched, IReadOnlyList<string> remaining) = RestoreLogs.Split(
			[
				new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, first),
				new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, second),
			],
			warnings);

		Assert.Equal(new[] { $"NU1903: {Vulnerability}" }, matched);
		Assert.Empty(remaining);
	}

	private static (string Message, string Code) SingleWarning(IReadOnlyDictionary<string, string> warnings)
	{
		KeyValuePair<string, string> single = Assert.Single(warnings);
		return (single.Key, single.Value);
	}

	private static (string Code, string Level, string Message) Warning(string code, string message) => (code, "Warning", message);

	private static IReadOnlyDictionary<string, string> WarningsOf(string message, string code) =>
		new Dictionary<string, string>(StringComparer.Ordinal) { [message] = code };

	/// <summary>A throwaway directory of synthetic assets files, removed when the test ends.</summary>
	private sealed class Scratch : IDisposable
	{
		public Scratch()
		{
			Root = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
		}

		public string Root { get; }

		/// <summary>A project file path under the scratch root, with its directory created and no assets file.</summary>
		public string Project(string name)
		{
			string directory = Path.Combine(Root, name);
			Directory.CreateDirectory(directory);
			return Path.Combine(directory, $"{name}.csproj");
		}

		/// <summary>Writes <c>obj/project.assets.json</c> beside the project file.</summary>
		public void WriteAssets(string projectFilePath, params (string Code, string Level, string Message)[] logs) =>
			WriteAssetsAt(Path.Combine(Path.GetDirectoryName(projectFilePath)!, "obj"), logs);

		/// <summary>Writes <c>project.assets.json</c> in a directory relative to the scratch root.</summary>
		public void WriteAssetsAt(string relativeDirectory, params (string Code, string Level, string Message)[] logs)
		{
			string directory = Path.Combine(Root, relativeDirectory);
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, RestoreLogs.AssetsFileName),
				JsonSerializer.Serialize(new
				{
					version = 3,
					logs = logs.Select(log => new { code = log.Code, level = log.Level, message = log.Message }),
				}));
		}

		public void WriteRawAssets(string projectFilePath, string content)
		{
			string directory = Path.Combine(Path.GetDirectoryName(projectFilePath)!, "obj");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, RestoreLogs.AssetsFileName), content);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
