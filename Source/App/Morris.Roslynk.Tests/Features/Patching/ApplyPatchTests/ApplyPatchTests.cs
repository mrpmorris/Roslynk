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

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
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

		string result = await subject.ApplyPatch(solutionPath, patch, baseVersions: null, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenABaseVersionIsStale_ThenItIsRejectedAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = BuildFullReplacePatch(GreeterRelativePath, original, original + "// patched\n");
		var staleVersions = new[] { new FileVersion(GreeterRelativePath, "0000DEADBEEF") };

		string result = await subject.ApplyPatch(solutionPath, patch, staleVersions);

		Assert.Contains("error=Stale", result);
		Assert.Contains("stale=", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenThePatchTargetsAProjectFile_ThenItIsAppliedToDisk()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string projectFile = FindFile(solutionPath, "SimpleLibrary.csproj");
		string original = await File.ReadAllTextAsync(projectFile);
		string patch = BuildFullReplacePatch("SimpleLibrary/SimpleLibrary.csproj", original, original + "<!-- patched -->\n");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.Contains("<!-- patched -->", await File.ReadAllTextAsync(projectFile));
	}

	[Fact]
	public async Task WhenThePatchTargetsARazorFile_ThenItIsAppliedAndTheSnapshotIsUpdated()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string counter = FindFile(solutionPath, "Counter.razor");
		string original = await File.ReadAllTextAsync(counter);
		string patch = BuildFullReplacePatch("RazorLib/Counter.razor", original, original + "@* patched *@\n");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.Contains("@* patched *@", await File.ReadAllTextAsync(counter));
		Assert.Contains("@* patched *@", await ReadAdditionalSnapshotTextAsync(instance, counter));
	}

	[Fact]
	public async Task WhenThePatchTargetsATextFileOutsideTheModel_ThenItIsAppliedToDisk()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string readme = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "README.txt");
		File.WriteAllText(readme, "hello\n");

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("SimpleLibrary/README.txt", "hello\n", "hello\nworld\n"));

		Assert.Contains("applied=Y", result);
		Assert.Equal("hello\nworld\n", await File.ReadAllTextAsync(readme));
	}

	[Fact]
	public async Task WhenThePatchTargetsABinaryFile_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string binary = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "data.bin");
		File.WriteAllBytes(binary, [0x00, 0x41, 0x42, 0x43]);
		string patch = BuildFullReplacePatch("SimpleLibrary/data.bin", "\u0000ABC", "x");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenThePatchTargetsAFileUnderObj_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string objFile = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "obj", "sample.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(objFile)!);
		File.WriteAllText(objFile, "hello\n");

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("SimpleLibrary/obj/sample.txt", "hello\n", "world\n"));

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenThePatchTargetsAFileOutsideTheSolutionFolder_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(solutionPath))!, "outside.txt");
		File.WriteAllText(outside, "hello\n");

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("../outside.txt", "hello\n", "world\n"));

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenThePatchTargetsANonExistentFile_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("SimpleLibrary/Missing.cs", "anything\n", "else\n"));

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenThePatchCreatesANewFile_ThenItIsNotSupported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			"--- /dev/null\n+++ b/SimpleLibrary/New.txt\n@@ -0,0 +1,1 @@\n+hello\n");

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
		Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "New.txt")));
	}

	[Fact]
	public async Task WhenAHunkDoesNotMatch_ThenAConflictIsReportedAndNothingIsWritten()
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

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=Conflict", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ApplyPatchTool(registry);

		string result = await subject.ApplyPatch(
			TestSolutions.Simple,
			$"--- a/{GreeterRelativePath}\n+++ b/{GreeterRelativePath}\n@@ -1,1 +1,1 @@\n-a\n+b\n");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenTheHunkHeaderHasNoLineNumbers_ThenTheFileIsStillUpdated()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = BuildBareFullReplacePatch(GreeterRelativePath, original, original + "// patched\n");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.Contains("// patched", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenAFilePatchHasNoHunks_ThenItIsRejected()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = $"--- a/{GreeterRelativePath}\n+++ b/{GreeterRelativePath}\n";

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=Invalid", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	private static async Task<string> ReadSnapshotTextAsync(RoslynInstance instance, string path)
	{
		Microsoft.CodeAnalysis.Solution solution = instance.CurrentSolution;
		Microsoft.CodeAnalysis.DocumentId id = solution.GetDocumentIdsWithFilePath(path).First();
		return (await solution.GetDocument(id)!.GetTextAsync()).ToString();
	}

	private static async Task<string> ReadAdditionalSnapshotTextAsync(RoslynInstance instance, string path)
	{
		Microsoft.CodeAnalysis.Solution solution = instance.CurrentSolution;
		Microsoft.CodeAnalysis.DocumentId id = solution.GetDocumentIdsWithFilePath(path)
			.First(documentId => solution.GetAdditionalDocument(documentId) is not null);
		return (await solution.GetAdditionalDocument(id)!.GetTextAsync()).ToString();
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

	private static string BuildBareFullReplacePatch(string relativePath, string originalText, string newText)
	{
		List<string> oldLines = SplitWithoutEol(originalText);
		List<string> newLines = SplitWithoutEol(newText);

		var builder = new StringBuilder();
		builder.Append($"--- a/{relativePath}\n");
		builder.Append($"+++ b/{relativePath}\n");
		builder.Append("@@\n");
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
