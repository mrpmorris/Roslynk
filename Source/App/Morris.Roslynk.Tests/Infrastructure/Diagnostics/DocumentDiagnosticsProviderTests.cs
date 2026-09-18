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

	private static async Task WithGreeterAsync(Func<Document, DocumentDiagnosticsProvider, Task> body)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.CodeStyle);
		Document document = CodeActionService.FindDocument(instance.CurrentSolution, "Greeter.cs")
			?? throw new InvalidOperationException("The CodeStyleSolution fixture no longer contains Greeter.cs.");

		await body(document, new DocumentDiagnosticsProvider());
	}
}
