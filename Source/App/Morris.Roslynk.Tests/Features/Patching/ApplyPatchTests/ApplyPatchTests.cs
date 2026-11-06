using System.Text;
using Morris.Roslynk.Features.Patching.ApplyPatch;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Patching.ApplyPatchTests;

public class ApplyPatchTests : MutatingTestBase
{
	private const string GreeterRelativePath = "SimpleLibrary/Greeter.cs";

	[Fact]
	public async Task WhenApplyingAValidPatch_ThenTheFileAndSnapshotAreUpdated()
	{
		var (instance, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (instance, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchRazorSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string binary = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "data.bin");
		File.WriteAllBytes(binary, [0x00, 0x41, 0x42, 0x43]);
		string patch = BuildFullReplacePatch("SimpleLibrary/data.bin", "\u0000ABC", "x");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenAPatchedFileHasAUtf8Bom_ThenTheBomIsPreserved()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		await File.WriteAllTextAsync(greeter, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(solutionPath, BuildFullReplacePatch(GreeterRelativePath, original, original + "// patched\n"));

		Assert.Contains("applied=Y", result);
		byte[] bytes = await File.ReadAllBytesAsync(greeter);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
		Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Skip(3).Take(3));
		Assert.Contains("// patched", Encoding.UTF8.GetString(bytes));
	}

	[Fact]
	public async Task WhenAPatchedFileIsUtf16WithABom_ThenItStaysUtf16()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string notes = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "notes.txt");
		await File.WriteAllTextAsync(notes, "first\n", Encoding.Unicode);

		string result = await subject.ApplyPatch(solutionPath, BuildFullReplacePatch("SimpleLibrary/notes.txt", "first\n", "second\n"));

		Assert.Contains("applied=Y", result);
		byte[] bytes = await File.ReadAllBytesAsync(notes);
		Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes.Take(2));
		Assert.Equal("second\n", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
	}

	[Fact]
	public async Task WhenAFileWithoutABomIsNotValidUtf8_ThenItIsNotSupported()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string notes = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "notes.txt");
		File.WriteAllBytes(notes, [0x41, 0xC3, 0x28, 0x0A]);
		string patch = BuildFullReplacePatch("SimpleLibrary/notes.txt", "A�(\n", "x\n");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=NotSupported", result);
		Assert.Equal(new byte[] { 0x41, 0xC3, 0x28, 0x0A }, await File.ReadAllBytesAsync(notes));
	}

	[Fact]
	public async Task WhenThePatchTargetsAFileUnderObj_ThenItIsNotSupported()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(solutionPath))!, "outside.txt");
		File.WriteAllText(outside, "hello\n");

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("../outside.txt", "hello\n", "world\n"));

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("rejected=", result);
	}

	[Fact]
	public async Task WhenThePatchTargetsANonExistentFile_ThenItIsNotFound()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("SimpleLibrary/Missing.cs", "anything\n", "else\n"));

		Assert.Contains("error=NotFound", result);
		Assert.Contains("rejected=SimpleLibrary/Missing.cs", result);
		Assert.Contains("relative to the solution folder", result);
	}

	[Fact]
	public async Task WhenAPathIsRelativeToTheRepoRootAndTheProjectIsMultiTargeted_ThenThePatchResolves()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchMultiTargetSolution());
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			"--- a/src/Multi/Api.cs\n+++ b/src/Multi/Api.cs\n@@\n-    public int Count() => 0;\n+    public int Count() => 1;\n",
			checkOnly: true);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("applied=N", result);
	}

	[Fact]
	public async Task WhenAPathIsRelativeToTheRepoRootAndTheProjectIsMultiTargeted_ThenTheFileIsPatchedOnce()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchMultiTargetSolution());
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			"--- a/src/Multi/Api.cs\n+++ b/src/Multi/Api.cs\n@@\n-    public int Count() => 0;\n+    public int Count() => 1;\n");

		Assert.Contains("applied=Y", result);
		string api = Path.Combine(Path.GetDirectoryName(solutionPath)!, "Multi", "Api.cs");
		Assert.Contains("Count() => 1;", await File.ReadAllTextAsync(api));
	}

	[Fact]
	public async Task WhenASuffixMatchesTwoDistinctFiles_ThenTheErrorIsAmbiguousWithCandidates()
	{
		string solutionPath = Scratch(() => TestSolutions.CreateScratchMultiTargetSolution());
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "Multi");
		foreach (string folder in new[] { "A", "B" })
		{
			Directory.CreateDirectory(Path.Combine(projectDirectory, folder));
			File.WriteAllText(Path.Combine(projectDirectory, folder, "Shared.cs"), "namespace Repro." + folder + ";\n\npublic class Shared { }\n");
		}

		await Registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("Shared.cs", "public class Shared { }\n", "public class Shared { public int X; }\n"));

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("rejected=Shared.cs", result);
		Assert.Contains("candidate=Multi/A/Shared.cs", result);
		Assert.Contains("candidate=Multi/B/Shared.cs", result);
	}

	[Fact]
	public async Task WhenRejectionsHaveDifferentReasons_ThenTheMostActionableCodeIsReportedWithEveryReason()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			solutionPath,
			BuildFullReplacePatch("SimpleLibrary/Missing.cs", "a\n", "b\n")
			+ "--- /dev/null\n+++ b/SimpleLibrary/New.txt\n@@ -0,0 +1,1 @@\n+hello\n");

		Assert.Contains("error=NotFound", result);
		Assert.Contains("rejected=SimpleLibrary/Missing.cs", result);
		Assert.Contains("rejected=SimpleLibrary/New.txt", result);
		Assert.Contains("creation/deletion", result);
	}

	[Fact]
	public async Task WhenThePatchCreatesANewFile_ThenItIsNotSupported()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
	public async Task WhenTheLoadWaitTimeoutElapses_ThenIndexingIsReturned()
	{
		using var Registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));
		var subject = new ApplyPatchTool(Registry);

		string result = await subject.ApplyPatch(
			TestSolutions.Simple,
			$"--- a/{GreeterRelativePath}\n+++ b/{GreeterRelativePath}\n@@ -1,1 +1,1 @@\n-a\n+b\n");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await Registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenTheHunkHeaderHasNoLineNumbers_ThenTheFileIsStillUpdated()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

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
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);
		string patch = $"--- a/{GreeterRelativePath}\n+++ b/{GreeterRelativePath}\n";

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=Invalid", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenAPatchRenamesACodeBehindParameter_ThenDependentRazorIsRegeneratedByTheSameWrite()
	{
		// App's Page.razor still says Count="3". The patch's own write regenerates App's Razor, so Count becomes a
		// plain attribute at once; no watcher event is needed (its fold would find the snapshot already current).
		var (instance, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchRazorMultiProjectSolution());
		var subject = new ApplyPatchTool(Registry);

		string codeBehind = FindFile(solutionPath, "Widget.razor.cs");
		string original = await File.ReadAllTextAsync(codeBehind);
		string result = await subject.ApplyPatch(solutionPath, BuildFullReplacePatch("Lib/Widget.razor.cs", original, original.Replace("Count", "Total")));

		Assert.Contains("applied=Y", result);
		Microsoft.CodeAnalysis.Project app = instance.CurrentSolution.Projects.Single(project => project.Name == "App");
		Microsoft.CodeAnalysis.Compilation compilation = (await app.GetCompilationAsync())!;
		Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
	}

	[Fact]
	public async Task WhenAPatchHasTwoSectionsForTheSameFile_ThenBothAreApplied()
	{
		var (instance, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string patch = BuildTwoSectionGreeterPatch();

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("error=", result);
		string disk = await File.ReadAllTextAsync(greeter);
		Assert.Contains("// first", disk);
		Assert.Contains("Hi, {name}", disk);
		Assert.Contains("// first", await ReadSnapshotTextAsync(instance, greeter));
		Assert.Contains("Hi, {name}", await ReadSnapshotTextAsync(instance, greeter));
		Assert.False(File.Exists(greeter + ".roslynk.tmp"));
		Assert.False(File.Exists(greeter + ".roslynk.bak"));
	}

	[Fact]
	public async Task WhenASecondSectionDependsOnTheFirst_ThenItAppliesToTheFirstSectionsResult()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string patch =
			$"--- a/{GreeterRelativePath}\n" +
			$"+++ b/{GreeterRelativePath}\n" +
			"@@\n" +
			" {\n" +
			"+	// first\n" +
			" 	/// <inheritdoc/>\n" +
			$"--- a/{GreeterRelativePath}\n" +
			$"+++ b/{GreeterRelativePath}\n" +
			"@@\n" +
			" 	// first\n" +
			"+	// second\n";

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("error=", result);
		string disk = (await File.ReadAllTextAsync(greeter)).Replace("\r\n", "\n");
		Assert.Contains("// first\n	// second\n", disk);
	}

	[Fact]
	public async Task WhenCheckingOnlyAPatchWithTwoSectionsForTheSameFile_ThenItSucceedsAndNothingIsWritten()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		string original = await File.ReadAllTextAsync(greeter);

		string result = await subject.ApplyPatch(solutionPath, BuildTwoSectionGreeterPatch(), baseVersions: null, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.DoesNotContain("error=", result);
		Assert.Equal(original, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenTheSecondSectionForAFileConflicts_ThenAConflictIsReportedAndNothingIsWritten()
	{
		var (_, solutionPath) = await ScratchAsync(() => TestSolutions.CreateScratchSimpleSolution());
		var subject = new ApplyPatchTool(Registry);

		string greeter = FindFile(solutionPath, "Greeter.cs");
		byte[] original = await File.ReadAllBytesAsync(greeter);
		string patch =
			$"--- a/{GreeterRelativePath}\n" +
			$"+++ b/{GreeterRelativePath}\n" +
			"@@\n" +
			" {\n" +
			"+	// first\n" +
			" 	/// <inheritdoc/>\n" +
			$"--- a/{GreeterRelativePath}\n" +
			$"+++ b/{GreeterRelativePath}\n" +
			"@@\n" +
			"-	this line does not exist in the file\n" +
			"+	replacement\n";

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("error=Conflict", result);
		Assert.Equal(original, await File.ReadAllBytesAsync(greeter));
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

	private static string BuildTwoSectionGreeterPatch() =>
		$"--- a/{GreeterRelativePath}\n" +
		$"+++ b/{GreeterRelativePath}\n" +
		"@@\n" +
		" {\n" +
		"+	// first\n" +
		" 	/// <inheritdoc/>\n" +
		$"--- a/{GreeterRelativePath}\n" +
		$"+++ b/{GreeterRelativePath}\n" +
		"@@\n" +
		"-	public string Greet(string name) => $\"Hello, {name}!\";\n" +
		"+	public string Greet(string name) => $\"Hi, {name}!\";\n";

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
