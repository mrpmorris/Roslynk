using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// The diagnostics a code fix can be offered for in one document: the compiler's, plus the analyzer
/// diagnostics <c>get_diagnostics</c> lists, so the two paths agree on what exists (IDE0005 and friends).
/// <para>
/// The whole document is always analysed and the whole document's diagnostics are returned; callers filter
/// by span. Passing a span to <see cref="CompilationWithAnalyzers"/> would filter what is reported rather
/// than what runs - so it saves little - and would drop diagnostics whose location is not where the caret
/// is: IDE0005 reports on the using directive at the top of the file, IDE0130 on the namespace, and so on.
/// </para>
/// <para>
/// Only analyzers that can report a fixable id are run, and only per-document analyzer APIs are used, so
/// this costs roughly one semantic pass over one file rather than a solution-wide analyzer run. The
/// trade-off is that diagnostics reported solely from a compilation-end action (many CA rules) do not
/// appear here; those rarely have a span-anchored fixer.
/// </para>
/// </summary>
public sealed class DocumentDiagnosticsProvider
{
	/// <summary>How long the analyzer pass may take before we fall back to compiler diagnostics alone.</summary>
	private static readonly TimeSpan AnalyzerBudget = TimeSpan.FromSeconds(5);

	private const int MaxCachedDocuments = 4;

	private readonly object Gate = new();
	private readonly LinkedList<CacheEntry> Cache = new();
	private DriverEntry? Driver;

	/// <summary>
	/// Compiler and analyzer diagnostics for <paramref name="document"/>'s tree. Never throws because of an
	/// analyzer: on any analyzer failure or budget overrun the compiler diagnostics are returned alone.
	/// </summary>
	public async Task<ImmutableArray<Diagnostic>> GetForDocumentAsync(Document document, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);

		VersionStamp version = await document.Project.GetDependentSemanticVersionAsync(cancellationToken);
		var key = new CacheKey(document.Id, version);

		Task<ImmutableArray<Diagnostic>> pending;
		lock (Gate)
		{
			pending = Take(key) ?? Add(key, document, version);
		}

		return await pending;
	}

	private Task<ImmutableArray<Diagnostic>>? Take(CacheKey key)
	{
		for (LinkedListNode<CacheEntry>? node = Cache.First; node is not null; node = node.Next)
		{
			if (node.Value.Key != key)
				continue;

			Cache.Remove(node);
			Cache.AddFirst(node);
			return node.Value.Diagnostics;
		}

		return null;
	}

	private Task<ImmutableArray<Diagnostic>> Add(CacheKey key, Document document, VersionStamp version)
	{
		// Started inside the lock so concurrent callers share one analyzer run rather than racing two. The
		// caller's token is not passed on: the task is shared, so one caller giving up must not cancel it.
		Task<ImmutableArray<Diagnostic>> task = ComputeAsync(document, version, CancellationToken.None);
		Cache.AddFirst(new CacheEntry(key, task));
		while (Cache.Count > MaxCachedDocuments)
			Cache.RemoveLast();

		return task;
	}

	private async Task<ImmutableArray<Diagnostic>> ComputeAsync(Document document, VersionStamp version, CancellationToken cancellationToken)
	{
		using Activity? activity = RoslynkActivitySource.Instance.StartActivity("document_diagnostics");

		Project project = document.Project;
		Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
		SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
		if (compilation is null || tree is null)
			return [];

		// Whole-compilation, not the semantic model's span: compilation-completion diagnostics such as
		// CS8019 (unnecessary using) only appear from here.
		ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(cancellationToken)
			.Where(diagnostic => diagnostic.Location.SourceTree == tree)
			.ToImmutableArray();

		ImmutableArray<DiagnosticAnalyzer> analyzers = AnalyzerDriverFactory.Narrow(
			AnalyzerDriverFactory.AnalyzersFor(project),
			compilation,
			tree,
			CodeActionCatalog.Instance.FixableDiagnosticIds,
			cancellationToken);
		activity?.SetTag("roslynk.analyzer.count", analyzers.Length);
		if (analyzers.IsEmpty)
			return compilerDiagnostics;

		ImmutableArray<Diagnostic> analyzerDiagnostics = await AnalyzerDiagnosticsAsync(
			project, version, compilation, tree, document, analyzers, activity, cancellationToken);

		activity?.SetTag("roslynk.diagnostic.count", compilerDiagnostics.Length + analyzerDiagnostics.Length);
		return [.. compilerDiagnostics, .. analyzerDiagnostics];
	}

	private async Task<ImmutableArray<Diagnostic>> AnalyzerDiagnosticsAsync(
		Project project,
		VersionStamp version,
		Compilation compilation,
		SyntaxTree tree,
		Document document,
		ImmutableArray<DiagnosticAnalyzer> analyzers,
		Activity? activity,
		CancellationToken cancellationToken)
	{
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(AnalyzerBudget);

		try
		{
			CompilationWithAnalyzers driver = DriverFor(project, version, compilation, analyzers);
			SemanticModel? semanticModel = await document.GetSemanticModelAsync(budget.Token);
			if (semanticModel is null)
				return [];

			ImmutableArray<Diagnostic> syntax = await driver.GetAnalyzerSyntaxDiagnosticsAsync(tree, budget.Token);
			ImmutableArray<Diagnostic> semantic = await driver.GetAnalyzerSemanticDiagnosticsAsync(semanticModel, filterSpan: null, budget.Token);
			return [.. syntax, .. semantic];
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			activity?.SetTag("roslynk.analyzer.degraded", "budget");
			return [];
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			activity?.SetTag("roslynk.analyzer.degraded", exception.GetType().Name);
			return [];
		}
	}

	/// <summary>
	/// The driver for this project snapshot, reused across documents so the analyzer state built for one
	/// file is not rebuilt for the next. Exactly one is held: it pins a <see cref="Compilation"/>.
	/// </summary>
	private CompilationWithAnalyzers DriverFor(Project project, VersionStamp version, Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers)
	{
		lock (Gate)
		{
			DriverEntry? existing = Driver;
			if (existing is not null
				&& existing.Project == project.Id
				&& existing.Version == version
				&& existing.Analyzers.SequenceEqual(analyzers))
			{
				return existing.Driver;
			}

			CompilationWithAnalyzers driver = AnalyzerDriverFactory.Create(project, compilation, analyzers);
			Driver = new DriverEntry(project.Id, version, analyzers, driver);
			return driver;
		}
	}

	private readonly record struct CacheKey(DocumentId Document, VersionStamp Version);

	private sealed record CacheEntry(CacheKey Key, Task<ImmutableArray<Diagnostic>> Diagnostics);

	private sealed record DriverEntry(ProjectId Project, VersionStamp Version, ImmutableArray<DiagnosticAnalyzer> Analyzers, CompilationWithAnalyzers Driver);
}
