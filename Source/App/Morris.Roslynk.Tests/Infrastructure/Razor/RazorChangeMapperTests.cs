using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

public class RazorChangeMapperTests
{
	private const string RazorPath = @"C:\project\X.razor";

	// A generated document whose #line-mapped region copies the razor @code content verbatim, the way
	// the Razor compiler emits it. "CurrentCount" sits at the same column in both texts.
	private const string GeneratedText =
		"class Counter\n" +
		"{\n" +
		"#line 2 \"" + RazorPath + "\"\n" +
		"	private int CurrentCount;\n" +
		"#line default\n" +
		"}\n";

	private const string RazorText =
		"@code {\n" +
		"	private int CurrentCount;\n" +
		"}\n";

	private static Document CreateGeneratedDocument()
	{
		var workspace = new AdhocWorkspace();
		Project project = workspace.AddProject("Test", LanguageNames.CSharp);
		return workspace.AddDocument(project.Id, "X_razor.g.cs", SourceText.From(GeneratedText));
	}

	private static TextChange RenameChangeAt(string text, string oldName, string newName) =>
		new(new TextSpan(text.IndexOf(oldName, StringComparison.Ordinal), oldName.Length), newName);

	private static Func<string, Task<SourceText?>> ProviderReturning(string? razorText) =>
		_ => Task.FromResult<SourceText?>(razorText is null ? null : SourceText.From(razorText));

	[Fact]
	public async Task WhenAChangeLandsInAMappedRegion_ThenItIsMappedToTheRazorSpanAndVerified()
	{
		Document generated = CreateGeneratedDocument();
		TextChange change = RenameChangeAt(GeneratedText, "CurrentCount", "Total");

		IReadOnlyList<(string RazorPath, TextChange Change)> mapped =
			await RazorChangeMapper.MapChangesAsync(generated, [change], ProviderReturning(RazorText));

		(string razorPath, TextChange razorChange) = Assert.Single(mapped);
		Assert.Equal(RazorPath, razorPath);
		Assert.Equal("Total", razorChange.NewText);
		Assert.Equal(RazorText.IndexOf("CurrentCount", StringComparison.Ordinal), razorChange.Span.Start);
		Assert.Equal("CurrentCount".Length, razorChange.Span.Length);
	}

	[Fact]
	public async Task WhenAChangeLandsOutsideAnyMappedRegion_ThenItIsUnmappable()
	{
		Document generated = CreateGeneratedDocument();
		TextChange change = RenameChangeAt(GeneratedText, "Counter", "Renamed");

		var exception = await Assert.ThrowsAsync<RazorMappingException>(
			() => RazorChangeMapper.MapChangesAsync(generated, [change], ProviderReturning(RazorText)));

		Assert.Equal(RazorMappingFailure.Unmappable, exception.Kind);
	}

	[Fact]
	public async Task WhenTheRazorTextDiffersAtTheMappedLocation_ThenItIsATextMismatch()
	{
		Document generated = CreateGeneratedDocument();
		TextChange change = RenameChangeAt(GeneratedText, "CurrentCount", "Total");

		// Same shape and length, different identifier — the verification must reject it.
		string mismatched = RazorText.Replace("CurrentCount", "CurrentKount");
		var exception = await Assert.ThrowsAsync<RazorMappingException>(
			() => RazorChangeMapper.MapChangesAsync(generated, [change], ProviderReturning(mismatched)));

		Assert.Equal(RazorMappingFailure.TextMismatch, exception.Kind);
	}

	[Fact]
	public async Task WhenTheMappedLocationIsOutsideTheRazorText_ThenItIsATextMismatch()
	{
		Document generated = CreateGeneratedDocument();
		TextChange change = RenameChangeAt(GeneratedText, "CurrentCount", "Total");

		var exception = await Assert.ThrowsAsync<RazorMappingException>(
			() => RazorChangeMapper.MapChangesAsync(generated, [change], ProviderReturning("@* too short *@")));

		Assert.Equal(RazorMappingFailure.TextMismatch, exception.Kind);
	}

	[Fact]
	public async Task WhenTheRazorFileIsNotInTheWorkspace_ThenTheSourceIsMissing()
	{
		Document generated = CreateGeneratedDocument();
		TextChange change = RenameChangeAt(GeneratedText, "CurrentCount", "Total");

		var exception = await Assert.ThrowsAsync<RazorMappingException>(
			() => RazorChangeMapper.MapChangesAsync(generated, [change], ProviderReturning(null)));

		Assert.Equal(RazorMappingFailure.MissingSource, exception.Kind);
	}
}
