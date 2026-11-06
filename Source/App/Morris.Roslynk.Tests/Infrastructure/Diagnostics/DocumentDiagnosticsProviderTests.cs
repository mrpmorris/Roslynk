using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Diagnostics;

public class DocumentDiagnosticsProviderTests
{
	[Fact]
	public void WhenTheTestProjectConfiguresTheAnalyzerBudget_ThenItIsApplied() =>
		// The test csproj sets ROSLYNK_ANALYZER_BUDGET_SECONDS as a RuntimeHostConfigurationOption, which
		// lands in runtimeconfig.json's configProperties and reaches the engine through AppContext.GetData.
		Assert.Equal(TimeSpan.FromSeconds(60), DocumentDiagnosticsProvider.AnalyzerBudget);

	[Theory]
	[InlineData(null, 5)]
	[InlineData("", 5)]
	[InlineData("abc", 5)]
	[InlineData("0", 5)]
	[InlineData("-3", 5)]
	[InlineData("60", 60)]
	[InlineData("2.5", 2.5)]
	public void WhenTheBudgetSettingIs_ThenTheParsedBudgetIs(string? value, double expectedSeconds) =>
		// The parse is culture-invariant, so "2.5" is two and a half seconds whatever the machine's culture is.
		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DocumentDiagnosticsProvider.ParseAnalyzerBudget(value));

	[Fact]
	public async Task WhenTheDocumentHasAnUnnecessaryUsing_ThenIde0005IsReturned() =>
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> diagnostics = await subject.GetForDocumentAsync(document);

			Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "IDE0005");
		});

	[Fact]
	public async Task WhenTheDocumentHasACompilerDiagnostic_ThenItIsStillReturned() =>
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> diagnostics = await subject.GetForDocumentAsync(document);

			Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS8019");
		});

	[Fact]
	public async Task WhenTheProjectsCompilerDiagnosticsAreMemoized_ThenTheyAreReusedRatherThanRecompiled() =>
		// get_diagnostics and every document's code actions share one compiler pass per project.
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> memoized = await DiagnosticsService.GetCompilerDiagnosticsAsync(document.Project);
			ImmutableArray<Diagnostic> diagnostics = await subject.GetForDocumentAsync(document);

			Diagnostic unnecessaryUsing = diagnostics.First(diagnostic => diagnostic.Id == "CS8019");
			Assert.Contains(memoized, diagnostic => ReferenceEquals(diagnostic, unnecessaryUsing));
		});

	[Fact]
	public async Task WhenTheCaretIsElsewhereInTheFile_ThenTheUsingDiagnosticIsStillReturned() =>
		// IDE0005 reports on the using directive, so analysing only the span the caller asked about would
		// hide it whenever the caret sits in the body. The provider is deliberately whole-document.
		await WithGreeterAsync(async (document, subject) =>
		{
			SourceText text = await document.GetTextAsync();
			TextSpan body = CodeActionService.SpanFor(text, text.Lines.Count - 1, 1, null, null);

			Diagnostic? unnecessaryUsing = (await subject.GetForDocumentAsync(document))
				.FirstOrDefault(diagnostic => diagnostic.Id == "IDE0005");

			Assert.NotNull(unnecessaryUsing);
			Assert.False(unnecessaryUsing.Location.SourceSpan.IntersectsWith(body), "The fixture's caret line should not cover the using.");
		});

	[Fact]
	public async Task WhenTheSameDocumentIsRequestedTwice_ThenTheCachedResultIsReturned() =>
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> first = await subject.GetForDocumentAsync(document);
			ImmutableArray<Diagnostic> second = await subject.GetForDocumentAsync(document);

			Assert.True(first == second, "The second call should have returned the cached array.");
		});

	[Fact]
	public async Task WhenTheDocumentIsEdited_ThenTheCacheIsNotReused() =>
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> before = await subject.GetForDocumentAsync(document);

			Document edited = document.WithText(SourceText.From("namespace CodeStyleLibrary;\r\n\r\npublic class Greeter;\r\n"));
			ImmutableArray<Diagnostic> after = await subject.GetForDocumentAsync(edited);

			Assert.False(before == after, "The edited document should not have hit the cache.");
			Assert.DoesNotContain(after, diagnostic => diagnostic.Id == "IDE0005");
		});

	[Fact]
	public async Task WhenOnlyAMethodBodyIsEdited_ThenTheCacheIsNotReused() =>
		await WithGreeterAsync(async (document, subject) =>
		{
			ImmutableArray<Diagnostic> before = await subject.GetForDocumentAsync(document);
			Assert.Contains(before, diagnostic => diagnostic.Id == "IDE0005");

			SourceText text = await document.GetTextAsync();
			TextLine bodyLine = text.Lines.Single(line => line.ToString().Contains("public string Greet"));
			SourceText newText = text.WithChanges(new TextChange(
				bodyLine.Span,
				"	public string Greet(string name) => new StringBuilder(\"Hello, \").Append(name).Append('!').ToString();"));
			Document edited = document.WithText(newText);

			// The edit changes only a method body, so the semantic version the old cache key used is unchanged.
			Assert.Equal(
				await document.Project.GetDependentSemanticVersionAsync(),
				await edited.Project.GetDependentSemanticVersionAsync());

			ImmutableArray<Diagnostic> after = await subject.GetForDocumentAsync(edited);

			Assert.False(before == after, "The body-only edit should not have hit the cache.");
			Assert.DoesNotContain(after, diagnostic => diagnostic.Id == "IDE0005");
			Assert.DoesNotContain(after, diagnostic => diagnostic.Id == "CS8019");
		});

	private static async Task WithGreeterAsync(Func<Document, DocumentDiagnosticsProvider, Task> body)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.CodeStyle);
		Document document = CodeActionService.FindDocument(instance.CurrentSolution, "Greeter.cs")
			?? throw new InvalidOperationException("The CodeStyleSolution fixture no longer contains Greeter.cs.");

		await body(document, new DocumentDiagnosticsProvider());
	}
}
