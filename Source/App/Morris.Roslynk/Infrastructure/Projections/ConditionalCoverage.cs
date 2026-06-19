using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Projections;

/// <summary>A conditional branch (<c>#if</c>/<c>#elif</c>/<c>#else</c>) whose region is never compiled in any
/// known configuration — a likely typo, stale flag, or intentionally-disabled code.</summary>
public sealed record DeadConditional(string FilePath, int Line, int Column, string Directive, string Condition);

/// <summary>
/// Flags conditional-compilation branches that no <em>real</em> configuration ever takes. It evaluates each
/// <c>#if</c>/<c>#elif</c>/<c>#else</c> against the configurations the project actually builds — each loaded
/// project's preprocessor symbols (its loaded config) and the same set minus <c>DEBUG</c> (the release config)
/// — by re-parsing the file under each and reading Roslyn's <see cref="BranchingDirectiveTriviaSyntax.BranchTaken"/>,
/// which is correct for nested directives and <c>#elif</c> chains.
///
/// It deliberately does NOT use <see cref="ProjectionService"/>'s toggle projections: those define every
/// referenced symbol (e.g. a typo'd <c>#if WINODWS</c>) to expose branches for queries, which would hide
/// exactly the dead branches this is meant to surface. Reliability is bounded by the configs considered:
/// a symbol defined only in a config not loaded here (e.g. a TFM whose workload is missing, or a CI-injected
/// define) can produce a false positive — so the result is "possible" dead code, and intentional <c>#if false</c>
/// is reported like any other.
/// </summary>
public sealed class ConditionalCoverage
{
	public async Task<IReadOnlyList<DeadConditional>> FindNeverBuiltAsync(Solution solution, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		IReadOnlyList<string[]> configs = RealConfigSets(solution);
		if (configs.Count == 0)
			return [];

		var takenAnywhere = new Dictionary<string, bool>(StringComparer.Ordinal);
		var branches = new Dictionary<string, DeadConditional>(StringComparer.Ordinal);
		var analyzedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (Project project in solution.Projects)
		{
			if (project.ParseOptions is not CSharpParseOptions template)
				continue;

			foreach (Document document in project.Documents)
			{
				if (document.FilePath is not string path || !analyzedFiles.Add(path))
					continue;

				SourceText text = await document.GetTextAsync(cancellationToken);

				foreach (string[] symbols in configs)
				{
					SyntaxNode root = await CSharpSyntaxTree
						.ParseText(text, template.WithPreprocessorSymbols(symbols), path, cancellationToken: cancellationToken)
						.GetRootAsync(cancellationToken);

					foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
					{
						if (trivia.GetStructure() is not BranchingDirectiveTriviaSyntax branch)
							continue;

						(string Directive, string Condition)? described = branch switch
						{
							IfDirectiveTriviaSyntax @if => ("if", @if.Condition.ToString()),
							ElifDirectiveTriviaSyntax elif => ("elif", elif.Condition.ToString()),
							ElseDirectiveTriviaSyntax => ("else", "(else)"),
							_ => null
						};

						if (described is not { } directive)
							continue;

						string key = $"{path}|{trivia.SpanStart}";
						takenAnywhere[key] = takenAnywhere.GetValueOrDefault(key) || branch.BranchTaken;

						if (!branches.ContainsKey(key))
						{
							FileLinePositionSpan span = trivia.SyntaxTree!.GetDisplaySpan(trivia.Span);
							branches[key] = new DeadConditional(
								path,
								span.StartLinePosition.Line + 1,
								span.StartLinePosition.Character + 1,
								directive.Directive,
								directive.Condition);
						}
					}
				}
			}
		}

		return takenAnywhere
			.Where(entry => !entry.Value)
			.Select(entry => branches[entry.Key])
			.ToList();
	}

	/// <summary>Each loaded project's symbols (its loaded config) and that set minus <c>DEBUG</c> (release), deduped.</summary>
	private static IReadOnlyList<string[]> RealConfigSets(Solution solution)
	{
		var sets = new List<string[]>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Add(IEnumerable<string> symbols)
		{
			string[] ordered = symbols.Distinct(StringComparer.Ordinal).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
			if (seen.Add(string.Join(";", ordered)))
				sets.Add(ordered);
		}

		foreach (Project project in solution.Projects)
		{
			if (project.ParseOptions is not CSharpParseOptions options)
				continue;

			List<string> loaded = options.PreprocessorSymbolNames.ToList();
			Add(loaded);
			Add(loaded.Where(symbol => !string.Equals(symbol, "DEBUG", StringComparison.Ordinal)));
		}

		return sets;
	}
}
