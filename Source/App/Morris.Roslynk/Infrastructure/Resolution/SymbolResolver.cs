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

	/// <summary>The namespace-qualified name (no <c>global::</c> prefix) used as a symbol's identity.</summary>
	public static string FullyQualifiedName(ISymbol symbol) =>
		symbol.ToDisplayString(FullyQualifiedFormat);

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
	public async Task<IReadOnlyList<ISymbol>> FindByFullyQualifiedNameAsync(Solution solution, string name, CancellationToken cancellationToken = default)
	{
		if (!SymbolSignature.TryParse(name, out SymbolSignatureQuery query))
			return [];

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("resolve_symbol"))
		{
			activity?.SetTag("roslynk.symbol.name", ActivityTags.Truncate(name));
			activity?.SetTag("roslynk.symbol.signature", query.ListKind != ParameterListKind.None);

			var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
			var matches = new List<ISymbol>();

			// A declaration search is keyed on a member's own name, which an indexer does not have — it is
			// declared as 'this[]' and renders as 'this'. A bracketed query therefore searches for the
			// containing type and scans its indexers instead.
			string searchName = query.ListKind == ParameterListKind.Brackets
				? ContainingTypeName(query)
				: query.SimpleName;

			foreach (Project project in solution.Projects)
			{
				foreach (ISymbol symbol in await SymbolFinder.FindDeclarationsAsync(project, searchName, ignoreCase: false, cancellationToken))
				{
					foreach (ISymbol candidate in Expand(symbol, query))
					{
						if (SymbolSignature.Matches(candidate, query) && seen.Add(candidate))
							matches.Add(candidate);
					}
				}
			}

			IReadOnlyList<ISymbol> narrowed = SymbolSignature.Narrow(matches, query);
			activity?.SetTag("roslynk.match.count", narrowed.Count);
			return narrowed;
		}
	}

	/// <summary>
	/// The simple name of the type a bracketed query's indexer belongs to, generic argument list included so
	/// the declaration search still finds a generic type.
	/// </summary>
	private static string ContainingTypeName(SymbolSignatureQuery query)
	{
		string head = query.QualifiedName;
		int memberDot = head.LastIndexOf('.');
		string container = memberDot >= 0 ? head[..memberDot] : head;
		int containerDot = container.LastIndexOf('.');
		return containerDot >= 0 ? container[(containerDot + 1)..] : container;
	}

	/// <summary>
	/// The symbols a declaration hit stands for: itself, plus — for a bracketed query, whose search was for
	/// the containing type — that type's indexers.
	/// </summary>
	private static IEnumerable<ISymbol> Expand(ISymbol symbol, SymbolSignatureQuery query)
	{
		if (query.ListKind != ParameterListKind.Brackets)
			return [symbol];

		return symbol is INamedTypeSymbol type
			? type.GetMembers().OfType<IPropertySymbol>().Where(member => member.IsIndexer)
			: [];
	}

	/// <summary>
	/// Like <see cref="FindByFullyQualifiedNameAsync"/>, but if nothing matches in source it falls back to
	/// referenced-assembly metadata (BCL / NuGet) via <c>GetTypeByMetadataName</c>; so read tools can
	/// resolve, e.g., <c>System.String</c> or <c>System.String.Substring</c>. Generic arity is not
	/// inferred, so closed generic metadata types are out of scope for v1.
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

		int lastDot = query.QualifiedName.LastIndexOf('.');
		string? containerName = lastDot > 0 ? query.QualifiedName[..lastDot] : null;

		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			if (query.ListKind == ParameterListKind.None
				&& compilation.GetTypeByMetadataName(query.QualifiedName) is INamedTypeSymbol type)
			{
				Add(type);
			}

			if (containerName is not null && compilation.GetTypeByMetadataName(containerName) is INamedTypeSymbol container)
			{
				foreach (ISymbol member in container.GetMembers(query.SimpleName))
				{
					if (SymbolSignature.Matches(member, query))
						Add(member);
				}
			}
		}

		return SymbolSignature.Narrow(matches, query);
	}

	/// <summary>
	/// Ranked fully-qualified-name suggestions for a name that did not resolve exactly; source symbols
	/// whose simple name matches case-insensitively or by substring, best first. Used to turn a near-miss
	/// into actionable candidates rather than an empty result.
	/// </summary>
	public async Task<IReadOnlyList<string>> SuggestAsync(Solution solution, string name, int maxResults = 10, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return [];

		int lastDot = name.LastIndexOf('.');
		string simpleName = lastDot >= 0 ? name[(lastDot + 1)..] : name;
		if (simpleName.Length == 0)
			return [];

		var best = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Project project in solution.Projects)
		{
			foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(project, candidate => IsCandidate(candidate, simpleName), cancellationToken))
			{
				string fullyQualified = FullyQualifiedName(symbol);
				int score = Score(symbol.Name, simpleName);
				if (!best.TryGetValue(fullyQualified, out int existing) || score < existing)
					best[fullyQualified] = score;
			}
		}

		return best
			.OrderBy(entry => entry.Value)
			.ThenBy(entry => entry.Key, StringComparer.Ordinal)
			.Take(maxResults)
			.Select(entry => entry.Key)
			.ToArray();
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
