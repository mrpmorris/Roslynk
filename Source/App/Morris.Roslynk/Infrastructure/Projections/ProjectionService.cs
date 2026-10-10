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
/// not toggled. A variant toggles the symbol only in the projects that test it: the others parse identically
/// either way, so they keep their trees and only recompile where they reference a toggled project.
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

		IReadOnlyDictionary<string, HashSet<ProjectId>> referenced = await DiscoverConditionSymbolsAsync(cSharpProjects, cancellationToken);

		foreach ((string symbol, HashSet<ProjectId> testedIn) in referenced.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			int definedCount = cSharpProjects.Count(project => Symbols(project).Contains(symbol));
			bool definedInAll = definedCount == cSharpProjects.Count;
			bool undefinedInAll = definedCount == 0;

			// Mixed across projects => already covered by the per-TFM projects; nothing to add.
			if (!definedInAll && !undefinedInAll)
				continue;

			Solution variant = solution;
			foreach (Project project in cSharpProjects.Where(project => testedIn.Contains(project.Id)))
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
	/// Resolves <paramref name="name"/> in every projection and groups the results by <see cref="SymbolIdentityIndex"/>
	/// identity, so the same logical symbol appearing in several projections (e.g. one per TFM, or base plus a toggled
	/// variant) collapses to a single group. Each group carries every per-projection instance, so a follow-up
	/// query can run against the solution each instance belongs to.
	/// </summary>
	public Task<IReadOnlyList<IReadOnlyList<ProjectionSymbol>>> ResolveAsync(
		SymbolResolver resolver,
		IReadOnlyList<Projection> projections,
		string name,
		CancellationToken cancellationToken = default) =>
		ResolveAsync(resolver, projections, name, includeMetadata: false, accept: null, cancellationToken);

	/// <summary>
	/// Resolves the name in every projection and groups the results by identity. The strictest matching level
	/// reached by any projection decides for every projection, so a non-generic type declared only in an
	/// inactive #if branch still wins over a generic sibling, as it would within one projection. When no
	/// projection matched in source, <paramref name="includeMetadata"/> falls back to referenced-assembly
	/// types; <paramref name="accept"/> keeps only symbols the calling tool can act on (applied before a
	/// level is judged, so a type-only tool is not stopped by a namespace of the same name).
	/// </summary>
	public async Task<IReadOnlyList<IReadOnlyList<ProjectionSymbol>>> ResolveAsync(
		SymbolResolver resolver,
		IReadOnlyList<Projection> projections,
		string name,
		bool includeMetadata,
		Func<ISymbol, bool>? accept,
		CancellationToken cancellationToken = default)
	{
		if (resolver is null)
			throw new ArgumentNullException(nameof(resolver));
		if (projections is null)
			throw new ArgumentNullException(nameof(projections));

		var resolved = new List<(Projection Projection, IReadOnlyList<ISymbol> Symbols, SymbolSignature.SymbolMatchKind Matching)>();
		foreach (Projection projection in projections)
		{
			SymbolResolver.RankedSymbols ranked = await resolver.FindRankedAsync(projection.Solution, name, cancellationToken);
			IReadOnlyList<ISymbol> symbols = accept is null
				? ranked.Symbols
				: ranked.Symbols.Where(accept).ToList();
			if (symbols.Count > 0)
				resolved.Add((projection, symbols, ranked.Matching));
		}

		if (resolved.Count > 0)
		{
			SymbolSignature.SymbolMatchKind best = resolved.Min(item => item.Matching);
			resolved.RemoveAll(item => item.Matching != best);
		}
		else if (includeMetadata)
		{
			foreach (Projection projection in projections)
			{
				IReadOnlyList<ISymbol> metadata =
					await resolver.FindByFullyQualifiedNameWithMetadataAsync(projection.Solution, name, cancellationToken);
				List<ISymbol> accepted = metadata.Where(symbol => accept is null || accept(symbol)).ToList();
				if (accepted.Count > 0)
					resolved.Add((projection, accepted, SymbolSignature.SymbolMatchKind.Exact));
			}
		}

		var identities = new SymbolIdentityIndex();
		var groups = new List<List<ProjectionSymbol>>();
		foreach ((Projection projection, IReadOnlyList<ISymbol> symbols, _) in resolved)
		{
			foreach (ISymbol symbol in symbols)
			{
				int index = identities.GroupOf(symbol, out bool isNew);
				if (isNew)
					groups.Add([]);

				groups[index].Add(new ProjectionSymbol(projection, symbol));
			}
		}

		return groups.Select(group => (IReadOnlyList<ProjectionSymbol>)group).ToList();
	}

	/// <summary>
	/// A stable identity for a symbol across projections: the <see cref="SymbolSignature"/> rendering at
	/// <see cref="SignatureTier.FullyQualifiedWithRefKinds"/>, so the same member found in several
	/// projections groups
	/// together while distinct overloads stay separate. Sharing the renderer with the candidate strings
	/// tools emit is deliberate: two spellings of the same idea are how they drift apart.
	/// </summary>
	/// <remarks>
	/// The same fully-qualified name declared by two different projects keys the same and is therefore
	/// reported as one candidate, not two.
	/// </remarks>
	public static string KeyOf(ISymbol symbol) =>
		SymbolSignature.Of(symbol, SignatureTier.FullyQualifiedWithRefKinds);

	private static IReadOnlyCollection<string> Symbols(Project project) =>
		((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.ToArray();

	/// <summary>Every symbol an <c>#if</c>/<c>#elif</c> tests, with the projects that test it.</summary>
	private static async Task<IReadOnlyDictionary<string, HashSet<ProjectId>>> DiscoverConditionSymbolsAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
	{
		var symbols = new Dictionary<string, HashSet<ProjectId>>(StringComparer.Ordinal);

		foreach (Project project in projects)
		{
			foreach (Document document in project.Documents)
			{
				// The directive chain visits only directives (inactive branches' nested ones included), and a tree
				// without any is skipped outright; walking every trivia of every tree costs about a second per call
				// on a large solution.
				if (await document.GetSyntaxRootAsync(cancellationToken) is not CSharpSyntaxNode root || !root.ContainsDirectives)
					continue;

				for (DirectiveTriviaSyntax? directive = root.GetFirstDirective(); directive is not null; directive = directive.GetNextDirective())
				{
					ExpressionSyntax? condition = directive switch
					{
						IfDirectiveTriviaSyntax @if => @if.Condition,
						ElifDirectiveTriviaSyntax elif => elif.Condition,
						_ => null
					};

					if (condition is null)
						continue;

					foreach (IdentifierNameSyntax identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
					{
						if (!symbols.TryGetValue(identifier.Identifier.ValueText, out HashSet<ProjectId>? testedIn))
							symbols[identifier.Identifier.ValueText] = testedIn = [];
						testedIn.Add(project.Id);
					}
				}
			}
		}

		return symbols;
	}
}
