using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Infrastructure.Projections;

/// <summary>
/// Builds the set of <see cref="Projection"/>s a query should run against so that conditionally-compiled
/// (<c>#if</c>) branches are all covered, and resolves a symbol name into logical groups across them.
///
/// Two axes produce branch coverage:
/// <list type="bullet">
/// <item>The <b>TargetFramework</b> axis is already loaded — MSBuild gives one <see cref="Project"/> per TFM,
/// each with its own preprocessor symbols (ANDROID/WINDOWS/NETx_0…). Those branches are covered by simply
/// running against every project.</item>
/// <item>The <b>configuration</b> axis (e.g. <c>DEBUG</c>) is uniform across the loaded projects, so its other
/// branch is only visible by toggling the symbol. For each <c>#if</c> symbol that is defined (or undefined)
/// uniformly across every project, one derived projection flips it. Flipping one symbol at a time keeps this
/// linear, not the 2^N powerset.</item>
/// </list>
/// A symbol whose defined-state already varies across the loaded projects is covered by those projects and is
/// not toggled.
/// </summary>
public sealed class ProjectionService
{
	/// <summary>The base projection plus one derived projection per uniformly-(un)defined <c>#if</c> symbol.</summary>
	public async Task<IReadOnlyList<Projection>> BuildAsync(Solution solution, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		var projections = new List<Projection> { new("base", solution) };

		List<Project> cSharpProjects = solution.Projects
			.Where(project => project.ParseOptions is CSharpParseOptions)
			.ToList();
		if (cSharpProjects.Count == 0)
			return projections;

		IReadOnlyCollection<string> referenced = await DiscoverConditionSymbolsAsync(cSharpProjects, cancellationToken);

		foreach (string symbol in referenced.OrderBy(name => name, StringComparer.Ordinal))
		{
			int definedCount = cSharpProjects.Count(project => Symbols(project).Contains(symbol));
			bool definedInAll = definedCount == cSharpProjects.Count;
			bool undefinedInAll = definedCount == 0;

			// Mixed across projects => already covered by the per-TFM projects; nothing to add.
			if (!definedInAll && !undefinedInAll)
				continue;

			Solution variant = solution;
			foreach (Project project in cSharpProjects)
			{
				var parseOptions = (CSharpParseOptions)project.ParseOptions!;
				IEnumerable<string> toggled = definedInAll
					? parseOptions.PreprocessorSymbolNames.Where(name => !string.Equals(name, symbol, StringComparison.Ordinal))
					: parseOptions.PreprocessorSymbolNames.Append(symbol);
				variant = variant.WithProjectParseOptions(project.Id, parseOptions.WithPreprocessorSymbols(toggled));
			}

			projections.Add(new Projection(definedInAll ? $"!{symbol}" : symbol, variant));
		}

		return projections;
	}

	/// <summary>
	/// Resolves <paramref name="name"/> in every projection and groups the results by a stable, signature-aware
	/// key, so the same logical symbol appearing in several projections (e.g. one per TFM, or base + a toggled
	/// variant) collapses to a single group. Each group carries every per-projection instance, so a follow-up
	/// query can run against the solution each instance belongs to.
	/// </summary>
	public async Task<IReadOnlyList<IReadOnlyList<ProjectionSymbol>>> ResolveAsync(
		SymbolResolver resolver,
		IReadOnlyList<Projection> projections,
		string name,
		CancellationToken cancellationToken = default)
	{
		if (resolver is null)
			throw new ArgumentNullException(nameof(resolver));
		if (projections is null)
			throw new ArgumentNullException(nameof(projections));

		var groups = new Dictionary<string, List<ProjectionSymbol>>(StringComparer.Ordinal);
		var order = new List<string>();

		foreach (Projection projection in projections)
		{
			foreach (ISymbol symbol in await resolver.FindByFullyQualifiedNameAsync(projection.Solution, name, cancellationToken))
			{
				string key = KeyOf(symbol);
				if (!groups.TryGetValue(key, out List<ProjectionSymbol>? group))
				{
					group = [];
					groups[key] = group;
					order.Add(key);
				}

				group.Add(new ProjectionSymbol(projection, symbol));
			}
		}

		return order.Select(key => (IReadOnlyList<ProjectionSymbol>)groups[key]).ToList();
	}

	/// <summary>
	/// A stable identity for a symbol across projections: its fully-qualified name plus a parameter-type
	/// signature for methods/indexers, so the same member found in several projections groups together while
	/// distinct overloads stay separate.
	/// </summary>
	public static string KeyOf(ISymbol symbol)
	{
		string fullyQualified = SymbolResolver.FullyQualifiedName(symbol);
		return symbol switch
		{
			IMethodSymbol method => $"{fullyQualified}({string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString()))})",
			IPropertySymbol { IsIndexer: true } indexer => $"{fullyQualified}[{string.Join(",", indexer.Parameters.Select(parameter => parameter.Type.ToDisplayString()))}]",
			_ => fullyQualified
		};
	}

	private static IReadOnlyCollection<string> Symbols(Project project) =>
		((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.ToArray();

	private static async Task<IReadOnlyCollection<string>> DiscoverConditionSymbolsAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
	{
		var symbols = new HashSet<string>(StringComparer.Ordinal);

		foreach (Project project in projects)
		{
			foreach (Document document in project.Documents)
			{
				SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken);
				if (root is null)
					continue;

				foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
				{
					ExpressionSyntax? condition = trivia.GetStructure() switch
					{
						IfDirectiveTriviaSyntax @if => @if.Condition,
						ElifDirectiveTriviaSyntax elif => elif.Condition,
						_ => null
					};

					if (condition is null)
						continue;

					foreach (IdentifierNameSyntax identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
						symbols.Add(identifier.Identifier.ValueText);
				}
			}
		}

		return symbols;
	}
}
