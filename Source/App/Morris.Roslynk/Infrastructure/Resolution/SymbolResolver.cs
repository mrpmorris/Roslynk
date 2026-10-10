using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Resolves a caller-supplied symbol reference to Roslyn symbols; by fully-qualified (or simple) name,
/// or by a source position. Fuzzy scoring is layered on in a later pass.
/// </summary>
public sealed class SymbolResolver
{
	private static readonly SymbolDisplayFormat FullyQualifiedFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

	/// <summary>
	/// The namespace-qualified name (no <c>global::</c> prefix) used as a symbol's identity. A local function is
	/// named as a member of the member declaring it, <c>N.T.Method.local</c>, which Roslyn's own display (the
	/// bare <c>local</c>) does not do.
	/// </summary>
	public static string FullyQualifiedName(ISymbol symbol) =>
		symbol is IMethodSymbol local && LocalFunctions.IsLocalFunction(local)
			? $"{FullyQualifiedName(LocalFunctions.NamedContainer(local))}.{local.ToDisplayString(FullyQualifiedFormat)}"
			: symbol.ToDisplayString(FullyQualifiedFormat);

	/// <summary>
	/// The name a caller should send to reach this exact symbol: <see cref="FullyQualifiedName"/> plus a
	/// parameter-type list for a method or indexer. Tools echo this rather than the parameterless name, so
	/// every name a response carries can be sent straight back.
	/// </summary>
	public static string SignatureName(ISymbol symbol) =>
		SymbolSignature.Of(symbol);

	/// <summary>
	/// Every symbol the name matches. A name may carry a parameter list to target one overload
	/// (<c>N.T.M(int, string)</c>); written without one it matches every overload, so an ambiguous result
	/// is still reported for the bare name a caller is most likely to try first.
	/// </summary>
	public async Task<IReadOnlyList<ISymbol>> FindByFullyQualifiedNameAsync(Solution solution, string name, CancellationToken cancellationToken = default) =>
		(await FindRankedAsync(solution, name, cancellationToken)).Symbols;

	/// <summary>The symbols a name resolved to and the strictest matching level that produced them.</summary>
	internal sealed record RankedSymbols(IReadOnlyList<ISymbol> Symbols, SymbolSignature.SymbolMatchKind Matching)
	{
		public static RankedSymbols None { get; } = new([], SymbolSignature.SymbolMatchKind.Exact);
	}

	internal async Task<RankedSymbols> FindRankedAsync(Solution solution, string name, CancellationToken cancellationToken = default)
	{
		if (!SymbolSignature.TryParse(name, out SymbolSignatureQuery query))
			return RankedSymbols.None;

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("resolve_symbol"))
		{
			activity?.SetTag("roslynk.symbol.name", ActivityTags.Truncate(name));
			activity?.SetTag("roslynk.symbol.signature", query.ListKind != ParameterListKind.None);

			// A declaration search is keyed on a member's own name, which an indexer does not have — it is
			// declared as 'this[]' and renders as 'this'. A bracketed query therefore searches for the
			// containing type and scans its indexers instead.
			string searchName = query.ListKind == ParameterListKind.Brackets
				? ContainingTypeName(query)
				: query.SimpleName;

			// One indexed sweep (the index is keyed on the bare Name, so 'Box' finds Box<T> and every arity of
			// Multi), filtered twice: exactly first — so every candidate a tool emits still resolves verbatim —
			// then, only when nothing matched exactly, with declared type-parameter names compared as arities.
			var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
			var exact = new List<ISymbol>();
			var relaxed = new List<ISymbol>();
			foreach (Project project in solution.Projects)
			{
				foreach (ISymbol symbol in await SymbolFinder.FindDeclarationsAsync(project, searchName, ignoreCase: false, cancellationToken))
				{
					foreach (ISymbol candidate in Expand(symbol, query))
					{
						switch (SymbolSignature.Classify(candidate, query))
						{
							case SymbolSignature.SymbolMatchKind.Exact when seen.Add(candidate):
								exact.Add(candidate);
								break;
							case SymbolSignature.SymbolMatchKind.Relaxed when seen.Add(candidate):
								relaxed.Add(candidate);
								break;
						}
					}
				}
			}

			// Local functions are not in the declaration index. C# forbids a nested type and a member of the same
			// name in one type, so a name that found no member can only mean a local function. The re-check
			// classifies like the sweep: a container that only resolved relaxedly must not be rejected by a
			// leftover exact-only comparison.
			if (exact.Count == 0 && relaxed.Count == 0)
			{
				foreach (IMethodSymbol local in await FindLocalFunctionsAsync(solution, query, cancellationToken))
				{
					SymbolSignature.SymbolMatchKind kind = SymbolSignature.Classify(local, query);
					if (kind != SymbolSignature.SymbolMatchKind.None && seen.Add(local))
						(kind == SymbolSignature.SymbolMatchKind.Exact ? exact : relaxed).Add(local);
				}
			}

			if (exact.Count > 0)
			{
				IReadOnlyList<ISymbol> narrowed = SymbolSignature.Narrow(exact, query);
				activity?.SetTag("roslynk.match.count", narrowed.Count);
				return new RankedSymbols(narrowed, SymbolSignature.SymbolMatchKind.Exact);
			}

			if (relaxed.Count > 0)
			{
				IReadOnlyList<ISymbol> narrowed = SymbolSignature.Narrow(relaxed, query);
				activity?.SetTag("roslynk.match.count", narrowed.Count);
				activity?.SetTag("roslynk.match.level", nameof(SymbolSignature.SymbolMatchKind.Relaxed));
				return new RankedSymbols(narrowed, SymbolSignature.SymbolMatchKind.Relaxed);
			}

			return RankedSymbols.None;
		}
	}

	/// <summary>
	/// The local functions a query could name: for <c>N.T.M.local</c>, those named <c>local</c> declared in
	/// whatever <c>N.T.M</c> resolves to (itself possibly a local function, for <c>N.T.M.outer.local</c>); for a
	/// bare <c>local</c>, every local function of that name in the solution.
	/// </summary>
	private async Task<IReadOnlyList<IMethodSymbol>> FindLocalFunctionsAsync(Solution solution, SymbolSignatureQuery query, CancellationToken cancellationToken)
	{
		if (query.ListKind == ParameterListKind.Brackets)
			return [];

		if (!SymbolSignature.TryGetContainer(query, out string containerName))
			return await LocalFunctions.FindAllAsync(solution, name => string.Equals(name, query.SimpleName, StringComparison.Ordinal), cancellationToken);

		var found = new List<IMethodSymbol>();
		foreach (ISymbol container in await FindByFullyQualifiedNameAsync(solution, containerName, cancellationToken))
		{
			if (container is IMethodSymbol or IPropertySymbol or IEventSymbol)
				found.AddRange(await LocalFunctions.FindInAsync(solution, container, query.SimpleName, cancellationToken));
		}

		return found;
	}

	/// <summary>
	/// The simple name of the type a bracketed query's indexer belongs to, with any generic argument list or
	/// metadata arity suffix removed, so the declaration search is keyed on the identifier the index is keyed
	/// by. The dots are split at top level only, so a container written with type arguments
	/// ('Repro.Dictionary&lt;string, int&gt;.this[int]') does not split inside the angle brackets.
	/// </summary>
	private static string ContainingTypeName(SymbolSignatureQuery query) =>
		query.Segments.Count > 1 ? query.Segments[^2].Name : query.SimpleName;

	/// <summary>
	/// The symbols a declaration hit stands for: itself, plus — for a bracketed query, whose search was for
	/// the containing type — that type's indexers. A type hit also stands for its explicitly declared
	/// constructors, which a declaration search cannot find by name ('.ctor') but which render as
	/// <c>N.Type.Type(...)</c>.
	/// </summary>
	private static IEnumerable<ISymbol> Expand(ISymbol symbol, SymbolSignatureQuery query)
	{
		if (query.ListKind != ParameterListKind.Brackets)
		{
			return symbol is INamedTypeSymbol constructed
				? [symbol, .. constructed.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared)]
				: [symbol];
		}

		return symbol is INamedTypeSymbol type
			? type.GetMembers().OfType<IPropertySymbol>().Where(member => member.IsIndexer)
			: [];
	}

	/// <summary>
	/// Like <see cref="FindByFullyQualifiedNameAsync"/>, but if nothing matches in source it falls back to
	/// referenced-assembly metadata (BCL / NuGet) via <c>GetTypeByMetadataName</c>; so read tools can
	/// resolve, e.g., <c>System.String</c> or <c>System.String.Substring</c>. A generic type is looked up
	/// under its canonical <c>`n</c> metadata name however it was written ('System.Collections.Generic.List`1',
	/// 'List&lt;T&gt;'), and its members match exact-then-relaxed like source symbols do.
	/// </summary>
	public async Task<IReadOnlyList<ISymbol>> FindByFullyQualifiedNameWithMetadataAsync(Solution solution, string name, CancellationToken cancellationToken = default)
	{
		IReadOnlyList<ISymbol> source = await FindByFullyQualifiedNameAsync(solution, name, cancellationToken);
		if (source.Count > 0 || string.IsNullOrWhiteSpace(name))
			return source;

		if (!SymbolSignature.TryParse(name, out SymbolSignatureQuery query))
			return source;

		var matches = new List<ISymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Add(ISymbol symbol)
		{
			// Keyed on the signature so distinct overloads of a metadata member survive the dedupe.
			if (seen.Add(SymbolSignature.Of(symbol, SignatureTier.FullyQualifiedWithRefKinds)))
				matches.Add(symbol);
		}

		bool containerFound = SymbolSignature.TryGetContainer(query, out string containerName);

		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			if (query.ListKind == ParameterListKind.None
				&& compilation.GetTypeByMetadataName(SymbolSignature.MetadataNameOf(query.QualifiedName)) is INamedTypeSymbol type)
			{
				Add(type);
			}

			if (containerFound && compilation.GetTypeByMetadataName(SymbolSignature.MetadataNameOf(containerName)) is INamedTypeSymbol container)
			{
				foreach (ISymbol member in container.GetMembers(query.SimpleName))
				{
					if (SymbolSignature.Matches(member, query) || SymbolSignature.Classify(member, query) == SymbolSignature.SymbolMatchKind.Relaxed)
						Add(member);
				}
			}
		}

		return SymbolSignature.Narrow(matches, query);
	}

	/// <summary>
	/// Ranked fully-qualified-name suggestions for a name that did not resolve exactly; source symbols
	/// whose simple name matches case-insensitively or by substring, best first — with candidates whose
	/// containing type or namespace is the query's own container segment ranked ahead of everything else.
	/// Used to turn a near-miss into actionable candidates rather than an empty result.
	/// </summary>
	public async Task<IReadOnlyList<string>> SuggestAsync(Solution solution, string name, int maxResults = 10, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return [];

		string simpleName = SymbolSignature.TryParse(name, out SymbolSignatureQuery parsed)
			? parsed.SimpleName
			: name[(name.LastIndexOf('.') + 1)..];
		if (simpleName.Length == 0)
			return [];

		string? containerSegment = null;
		if (parsed is not null
			&& SymbolSignature.TryGetContainer(parsed, out string container))
		{
			IReadOnlyList<SymbolNameSegment> segments = SymbolSignature.SplitSegments(container);
			containerSegment = segments[^1].Name;
		}

		var best = new Dictionary<string, (int Score, bool InContainer)>(StringComparer.Ordinal);
		foreach (Project project in solution.Projects)
		{
			foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(project, candidate => IsCandidate(candidate, simpleName), cancellationToken))
			{
				string fullyQualified = FullyQualifiedName(symbol);
				(int Score, bool InContainer) rank = (Score(symbol.Name, simpleName), InQueriedContainer(symbol, containerSegment));
				if (!best.TryGetValue(fullyQualified, out (int, bool) existing) || Rank(rank) < Rank(existing))
					best[fullyQualified] = rank;
			}
		}

		foreach (IMethodSymbol local in await LocalFunctions.FindAllAsync(solution, candidate => IsCandidate(candidate, simpleName), cancellationToken))
		{
			string fullyQualified = FullyQualifiedName(local);
			(int Score, bool InContainer) rank = (Score(local.Name, simpleName), InQueriedContainer(LocalFunctions.NamedContainer(local), containerSegment));
			if (!best.TryGetValue(fullyQualified, out (int, bool) existing) || Rank(rank) < Rank(existing))
				best[fullyQualified] = rank;
		}

		return best
			.OrderBy(entry => Rank(entry.Value))
			.ThenBy(entry => entry.Key, StringComparer.Ordinal)
			.Take(maxResults)
			.Select(entry => entry.Key)
			.ToArray();

		static int Rank((int Score, bool InContainer) rank) => (rank.InContainer ? 0 : 4) + rank.Score;
	}

	/// <summary>
	/// Whether the symbol is declared in the container the query named, so its candidate is ranked ahead of
	/// same-shaped members of unrelated types. The containing namespace counts too, so a query naming a
	/// namespace lifts a type declared in it.
	/// </summary>
	private static bool InQueriedContainer(ISymbol symbol, string? containerSegment)
	{
		if (containerSegment is null)
			return false;

		if (symbol.ContainingType is { } containingType)
			return string.Equals(containingType.Name, containerSegment, StringComparison.OrdinalIgnoreCase);

		return symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace
			&& string.Equals(containingNamespace.Name, containerSegment, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCandidate(string candidate, string simpleName) =>
		candidate.Contains(simpleName, StringComparison.OrdinalIgnoreCase)
		|| simpleName.Contains(candidate, StringComparison.OrdinalIgnoreCase);

	private static int Score(string candidate, string simpleName)
	{
		if (string.Equals(candidate, simpleName, StringComparison.Ordinal))
			return 0;
		if (string.Equals(candidate, simpleName, StringComparison.OrdinalIgnoreCase))
			return 1;
		if (candidate.StartsWith(simpleName, StringComparison.OrdinalIgnoreCase))
			return 2;
		return 3;
	}

	/// <summary>
	/// Resolves the symbol referenced at a 1-based <paramref name="line"/>/<paramref name="column"/> in
	/// the given file, or null if the file is not in the solution or no symbol sits there.
	/// </summary>
	public async Task<ISymbol?> ResolveAtPositionAsync(Solution solution, string filePath, int line, int column, CancellationToken cancellationToken = default)
	{
		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("resolve_position"))
		{
			activity?.SetTag("roslynk.file.path", ActivityTags.Truncate(filePath));
			activity?.SetTag("roslynk.line", line);
			activity?.SetTag("roslynk.column", column);

			Document? document = FindDocument(solution, filePath);
			if (document is null)
				return null;

			SourceText text = await document.GetTextAsync(cancellationToken);
			if (line < 1 || line > text.Lines.Count)
				return null;

			TextLine textLine = text.Lines[line - 1];
			int position = Math.Min(textLine.Start + Math.Max(0, column - 1), textLine.End);

			SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken);
			if (semanticModel is null)
				return null;

			return await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, cancellationToken);
		}
	}

	private static Document? FindDocument(Solution solution, string filePath)
	{
		string fullPath = SolutionRelativePath.ToAbsolute(SolutionRelativePath.DirectoryOf(solution), filePath);
		foreach (Project project in solution.Projects)
		{
			foreach (Document document in project.Documents)
			{
				if (document.FilePath is not null
					&& string.Equals(Path.GetFullPath(document.FilePath), fullPath, StringComparison.OrdinalIgnoreCase))
				{
					return document;
				}
			}
		}

		return null;
	}
}
