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

	public async Task<IReadOnlyList<ISymbol>> FindByFullyQualifiedNameAsync(Solution solution, string name, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return [];

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("resolve_symbol"))
		{
			activity?.SetTag("roslynk.symbol.name", ActivityTags.Truncate(name));

			bool qualified = name.Contains('.');
			string simpleName = qualified ? name[(name.LastIndexOf('.') + 1)..] : name;

			var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
			var matches = new List<ISymbol>();

			foreach (Project project in solution.Projects)
			{
				foreach (ISymbol symbol in await SymbolFinder.FindDeclarationsAsync(project, simpleName, ignoreCase: false, cancellationToken))
				{
					bool isMatch = qualified
						? string.Equals(FullyQualifiedName(symbol), name, StringComparison.Ordinal)
						: string.Equals(symbol.Name, name, StringComparison.Ordinal);

					if (isMatch && seen.Add(symbol))
						matches.Add(symbol);
				}
			}

			activity?.SetTag("roslynk.match.count", matches.Count);
			return matches;
		}
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

		var matches = new List<ISymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		void Add(ISymbol symbol)
		{
			if (seen.Add(FullyQualifiedName(symbol)))
				matches.Add(symbol);
		}

		int lastDot = name.LastIndexOf('.');
		string? containerName = lastDot > 0 ? name[..lastDot] : null;
		string memberName = lastDot > 0 ? name[(lastDot + 1)..] : name;

		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			if (compilation.GetTypeByMetadataName(name) is INamedTypeSymbol type)
				Add(type);

			if (containerName is not null && compilation.GetTypeByMetadataName(containerName) is INamedTypeSymbol container)
			{
				foreach (ISymbol member in container.GetMembers(memberName))
					Add(member);
			}
		}

		return matches;
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
