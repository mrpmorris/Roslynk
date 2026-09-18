using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Infrastructure.Diagnostics;

/// <summary>
/// Builds the analyzer driver both diagnostic paths run on: the project's analyzers (optionally narrowed
/// to those that can report an id some code-fix provider handles) plus the options that make a
/// misbehaving third-party analyzer degrade rather than fail the tool.
/// </summary>
public static class AnalyzerDriverFactory
{
	/// <summary>Every analyzer the project references, skipping references that fail to enumerate.</summary>
	public static ImmutableArray<DiagnosticAnalyzer> AnalyzersFor(Project project)
	{
		var analyzers = new List<DiagnosticAnalyzer>();
		foreach (AnalyzerReference reference in project.AnalyzerReferences)
		{
			try
			{
				analyzers.AddRange(reference.GetAnalyzers(project.Language));
			}
			catch
			{
				// The reference cannot produce analyzers for this language; SolutionWorkspace has already
				// reported genuine load failures, so there is nothing to add here.
			}
		}

		return [.. analyzers];
	}

	/// <summary>
	/// The subset of <paramref name="analyzers"/> worth running when the caller only wants diagnostics it
	/// could offer a fix for: the analyzer must report at least one fixable id that is not suppressed for
	/// this tree. Severity is deliberately not required to reach warning - IDE0005 is hidden by default and
	/// is still fixable - so only outright suppression disqualifies a rule.
	/// </summary>
	public static ImmutableArray<DiagnosticAnalyzer> Narrow(
		ImmutableArray<DiagnosticAnalyzer> analyzers,
		Compilation compilation,
		SyntaxTree tree,
		ImmutableHashSet<string> fixableIds,
		CancellationToken cancellationToken)
	{
		var kept = new List<DiagnosticAnalyzer>();
		foreach (DiagnosticAnalyzer analyzer in analyzers)
		{
			foreach (DiagnosticDescriptor descriptor in SupportedDiagnostics(analyzer))
			{
				if (!fixableIds.Contains(descriptor.Id) || IsSuppressed(descriptor, compilation, tree, cancellationToken))
					continue;

				kept.Add(analyzer);
				break;
			}
		}

		return [.. kept];
	}

	/// <summary>
	/// The driver for <paramref name="analyzers"/>. <c>onAnalyzerException</c> is non-null on purpose: it is
	/// what makes Roslyn swallow an analyzer's fault instead of propagating it to the caller.
	/// </summary>
	public static CompilationWithAnalyzers Create(Project project, Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers) =>
		Create(compilation, analyzers, project.AnalyzerOptions);

	/// <inheritdoc cref="Create(Project, Compilation, ImmutableArray{DiagnosticAnalyzer})"/>
	public static CompilationWithAnalyzers Create(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions analyzerOptions)
	{
		var options = new CompilationWithAnalyzersOptions(
			options: analyzerOptions,
			onAnalyzerException: static (_, _, _) => { },
			concurrentAnalysis: true,
			logAnalyzerExecutionTime: false,
			reportSuppressedDiagnostics: false);
		return compilation.WithAnalyzers(analyzers, options);
	}

	/// <summary>
	/// Whether the rule is turned off for this tree, honouring editorconfig first (the channel the
	/// workspace wires <c>.editorconfig</c> into), then the project's compilation options, then the
	/// descriptor's own default.
	/// </summary>
	private static bool IsSuppressed(DiagnosticDescriptor descriptor, Compilation compilation, SyntaxTree tree, CancellationToken cancellationToken)
	{
		SyntaxTreeOptionsProvider? provider = compilation.Options.SyntaxTreeOptionsProvider;
		if (provider is not null && provider.TryGetDiagnosticValue(tree, descriptor.Id, cancellationToken, out ReportDiagnostic configured))
			return configured == ReportDiagnostic.Suppress;

		ReportDiagnostic effective = descriptor.GetEffectiveSeverity(compilation.Options);
		if (effective != ReportDiagnostic.Default)
			return effective == ReportDiagnostic.Suppress;

		return !descriptor.IsEnabledByDefault;
	}

	private static ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics(DiagnosticAnalyzer analyzer)
	{
		try
		{
			return analyzer.SupportedDiagnostics;
		}
		catch
		{
			return [];
		}
	}
}
