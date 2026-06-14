using Morris.Roslynk.Infrastructure.Patching;

namespace Morris.Roslynk.Tests.Infrastructure.Patching;

public class UnifiedDiffParserTests
{
	[Fact]
	public void WhenParsingASingleHunk_ThenPathsHeaderAndLinesAreRead()
	{
		const string patch =
			"diff --git a/Greeter.cs b/Greeter.cs\n" +
			"index 1111111..2222222 100644\n" +
			"--- a/Greeter.cs\n" +
			"+++ b/Greeter.cs\n" +
			"@@ -3,2 +3,2 @@ namespace SimpleLibrary;\n" +
			" \tpublic string Greet()\n" +
			"-\t\treturn \"Hello\";\n" +
			"+\t\treturn \"Hi\";\n";

		IReadOnlyList<FilePatch> result = UnifiedDiffParser.Parse(patch);

		FilePatch file = Assert.Single(result);
		Assert.Equal("Greeter.cs", file.NewPath);
		Assert.Equal("Greeter.cs", file.OldPath);
		Hunk hunk = Assert.Single(file.Hunks);
		Assert.Equal(3, hunk.OldStart);
		Assert.Equal(2, hunk.OldLength);
		Assert.Equal(3, hunk.Lines.Count);
		Assert.Equal(HunkLineKind.Context, hunk.Lines[0].Kind);
		Assert.Equal(HunkLineKind.Removed, hunk.Lines[1].Kind);
		Assert.Equal(HunkLineKind.Added, hunk.Lines[2].Kind);
	}

	[Fact]
	public void WhenAHunkHeaderOmitsLengths_ThenTheyDefaultToOne()
	{
		const string patch =
			"--- a/x.cs\n" +
			"+++ b/x.cs\n" +
			"@@ -5 +5 @@\n" +
			"-old\n" +
			"+new\n";

		FilePatch file = Assert.Single(UnifiedDiffParser.Parse(patch));

		Hunk hunk = Assert.Single(file.Hunks);
		Assert.Equal(1, hunk.OldLength);
		Assert.Equal(1, hunk.NewLength);
	}

	[Fact]
	public void WhenTheOldSideIsDevNull_ThenTheFileIsMarkedAsCreation()
	{
		const string patch =
			"--- /dev/null\n" +
			"+++ b/New.cs\n" +
			"@@ -0,0 +1,1 @@\n" +
			"+hello\n";

		FilePatch file = Assert.Single(UnifiedDiffParser.Parse(patch));

		Assert.True(file.IsCreation);
		Assert.Equal("New.cs", file.NewPath);
	}

	[Fact]
	public void WhenThePatchTouchesTwoFiles_ThenBothAreParsed()
	{
		const string patch =
			"--- a/A.cs\n" +
			"+++ b/A.cs\n" +
			"@@ -1 +1 @@\n" +
			"-a\n" +
			"+A\n" +
			"--- a/B.cs\n" +
			"+++ b/B.cs\n" +
			"@@ -1 +1 @@\n" +
			"-b\n" +
			"+B\n";

		IReadOnlyList<FilePatch> result = UnifiedDiffParser.Parse(patch);

		Assert.Equal(2, result.Count);
		Assert.Equal("A.cs", result[0].NewPath);
		Assert.Equal("B.cs", result[1].NewPath);
	}
}
