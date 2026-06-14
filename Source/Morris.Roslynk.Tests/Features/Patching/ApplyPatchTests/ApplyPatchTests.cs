using System.Text;
using Morris.Roslynk.Features.Patching.ApplyPatch;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Patching.ApplyPatchTests;

public class ApplyPatchTests
{
	private const string GreeterRelativePath = "SimpleLibrary/Greeter.cs";

	[Fact]
	public async Task WhenApplyingAValidPatch_ThenTheFileAndSnapshotAreUpdated()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = BuildFullReplacePatch(GreeterRelativePath, original, original + "// patched\n");

		ApplyPatchResponse response = await subject.ApplyPatch(solutionPath, patch);

		Assert.Equal(ApplyPatchOutcome.Applied, response.Outcome);
		Assert.Single(response.ChangedFiles);
		Assert.Contains("// patched", await File.ReadAllTextAsync(greeter));
		Assert.Contains("// patched", await ReadSnapshotTextAsync(instance, greeter));
	}

	[Fact]
	public async Task WhenApplyingWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = BuildFullReplacePatch(GreeterRelativePath, original, original + "// patched\n");

		ApplyPatchResponse response = await subject.ApplyPatch(solutionPath, patch, baseVersions: null, checkOnly: true);

		Assert.Equal(ApplyPatchOutcome.Preview, response.Outcome);
		Assert.Single(response.ChangedFiles);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenABaseVersionIsStale_ThenItIsRejectedWithCurrentContent()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = BuildFullReplacePatch(GreeterRelativePath, original, original + "// patched\n");
		var staleVersions = new[] { new FileVersion(GreeterRelativePath, "0000DEADBEEF") };

		ApplyPatchResponse response = await subject.ApplyPatch(solutionPath, patch, staleVersions);

		Assert.Equal(ApplyPatchOutcome.Stale, response.Outcome);
		ApplyPatchStaleFile stale = Assert.Single(response.StaleFiles);
		Assert.Equal(original, stale.CurrentText);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenThePatchTargetsANonSourceFile_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string patch = BuildFullReplacePatch("SimpleLibrary/SimpleLibrary.csproj", "<Project/>\n", "<Project></Project>\n");

		ApplyPatchResponse response = await subject.ApplyPatch(solutionPath, patch);

		Assert.Equal(ApplyPatchOutcome.NotSupported, response.Outcome);
		Assert.NotEmpty(response.RejectedFiles);
	}

	[Fact]
	public async Task WhenAHunkDoesNotMatch_ThenThePatchFailsAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch =
			$"--- a/{GreeterRelativePath}\n" +
			$"+++ b/{GreeterRelativePath}\n" +
			"@@ -1,1 +1,1 @@\n" +
			"-this content is not present anywhere\n" +
			"+replacement\n";

		ApplyPatchResponse response = await subject.ApplyPatch(solutionPath, patch);

		Assert.Equal(ApplyPatchOutcome.PatchFailed, response.Outcome);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	private static async Task<string> ReadSnapshotTextAsync(RoslynInstance instance, string path)
	{
		Microsoft.CodeAnalysis.Solution solution = instance.CurrentSolution;
		Microsoft.CodeAnalysis.DocumentId id = solution.GetDocumentIdsWithFilePath(path).First();
		return (await solution.GetDocument(id)!.GetTextAsync()).ToString();
	}

	private static string BuildFullReplacePatch(string relativePath, string originalText, string newText)
	{
		List<string> oldLines = SplitWithoutEol(originalText);
		List<string> newLines = SplitWithoutEol(newText);

		var builder = new StringBuilder();
		builder.Append($"--- a/{relativePath}\n");
		builder.Append($"+++ b/{relativePath}\n");
		builder.Append($"@@ -1,{oldLines.Count} +1,{newLines.Count} @@\n");
		foreach (string line in oldLines)
			builder.Append('-').Append(line).Append('\n');
		foreach (string line in newLines)
			builder.Append('+').Append(line).Append('\n');

		return builder.ToString();
	}

	private static List<string> SplitWithoutEol(string text)
	{
		if (text.Length == 0)
			return [];

		List<string> lines = [.. text.Replace("\r\n", "\n").Split('\n')];
		if (text.EndsWith('\n') && lines.Count > 0 && lines[^1].Length == 0)
			lines.RemoveAt(lines.Count - 1);

		return lines;
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
