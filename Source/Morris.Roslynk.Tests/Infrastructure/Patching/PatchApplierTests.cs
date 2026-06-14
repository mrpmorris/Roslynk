using Morris.Roslynk.Infrastructure.Patching;

namespace Morris.Roslynk.Tests.Infrastructure.Patching;

public class PatchApplierTests
{
	[Fact]
	public void WhenAHunkReplacesALine_ThenTheLineIsChanged()
	{
		string original = "line1\nline2\nline3\n";
		FilePatch patch = ParseSingle(
			"@@ -1,3 +1,3 @@\n" +
			" line1\n" +
			"-line2\n" +
			"+changed\n" +
			" line3\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.True(result.Success);
		Assert.Equal("line1\nchanged\nline3\n", result.NewText);
	}

	[Fact]
	public void WhenAHunkAddsLines_ThenTheyAreInserted()
	{
		string original = "a\nb\n";
		FilePatch patch = ParseSingle(
			"@@ -1,2 +1,3 @@\n" +
			" a\n" +
			"+inserted\n" +
			" b\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.True(result.Success);
		Assert.Equal("a\ninserted\nb\n", result.NewText);
	}

	[Fact]
	public void WhenTheHunkLineNumbersAreStale_ThenItRelocatesByContent()
	{
		string original = "x\nx\nx\ntarget\ntail\n";
		FilePatch patch = ParseSingle(
			"@@ -42,1 +42,1 @@\n" +
			"-target\n" +
			"+TARGET\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.True(result.Success);
		Assert.Equal("x\nx\nx\nTARGET\ntail\n", result.NewText);
	}

	[Fact]
	public void WhenTheContextNoLongerMatches_ThenApplyFails()
	{
		string original = "a\nb\nc\n";
		FilePatch patch = ParseSingle(
			"@@ -1,1 +1,1 @@\n" +
			"-not present anywhere\n" +
			"+replacement\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.False(result.Success);
		Assert.NotNull(result.FailureReason);
	}

	[Fact]
	public void WhenTheFileUsesCrlf_ThenThatLineEndingIsPreserved()
	{
		string original = "a\r\nb\r\nc\r\n";
		FilePatch patch = ParseSingle(
			"@@ -1,3 +1,3 @@\n" +
			" a\n" +
			"-b\n" +
			"+B\n" +
			" c\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.True(result.Success);
		Assert.Equal("a\r\nB\r\nc\r\n", result.NewText);
	}

	[Fact]
	public void WhenTheFileHasNoTrailingNewline_ThenNoneIsAdded()
	{
		string original = "a\nb";
		FilePatch patch = ParseSingle(
			"@@ -1,2 +1,2 @@\n" +
			" a\n" +
			"-b\n" +
			"+B\n");

		PatchApplyResult result = PatchApplier.Apply(original, patch);

		Assert.True(result.Success);
		Assert.Equal("a\nB", result.NewText);
	}

	private static FilePatch ParseSingle(string hunk) =>
		UnifiedDiffParser.Parse("--- a/x.cs\n+++ b/x.cs\n" + hunk).Single();
}
