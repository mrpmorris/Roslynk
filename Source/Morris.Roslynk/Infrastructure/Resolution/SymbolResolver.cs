using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Resolves a caller-supplied symbol name to Roslyn symbols. Currently matches by fully-qualified name
/// (or simple name); fuzzy scoring and position-based resolution are layered on in later passes.
/// </summary>
public sealed class SymbolResolver
{
	private static readonly SymbolDisplayFormat FullyQualifiedFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

	/// <summary>The namespace-qualified name (no <c>global::</c> prefix) used as a symbol's identity.</summary>
	public static string FullyQualifiedName(ISymbol symbol) =>
		symbol.ToDisplayString(FullyQualifiedFormat);

	public async Task<IReadOnlyList<ISymbol>> FindByFullyQualifiedNameAsync(Solution solution, string name, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return [];

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

		return matches;
	}
}
