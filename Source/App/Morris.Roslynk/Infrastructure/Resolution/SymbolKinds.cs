using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// The symbol kinds <c>search_symbols</c> can filter on, named exactly as the outline leaves report them
/// (see <see cref="SymbolKindText"/>). Parsing is case-insensitive, so the PascalCase spelling agents
/// commonly use ("Method") is accepted, but an unknown name is a caller error naming the accepted set
/// rather than a filter that quietly matches nothing.
/// </summary>
public static class SymbolKinds
{
	public static readonly IReadOnlyList<string> Accepted =
	[
		"namespace",
		"class",
		"struct",
		"interface",
		"enum",
		"delegate",
		"module",
		"method",
		"property",
		"field",
		"event",
		"localfunction",
	];

	/// <summary>
	/// The kind set to filter on, or null when no filter was requested. An empty or blank list means
	/// "all kinds", matching the outline's own empty-list convention. Throws <see cref="FormatException"/>
	/// for a name outside <see cref="Accepted"/>, naming the parameter and the accepted names.
	/// </summary>
	public static HashSet<string>? Parse(string[]? kinds)
	{
		if (kinds is not { Length: > 0 })
			return null;

		var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string? kind in kinds)
		{
			if (string.IsNullOrWhiteSpace(kind))
				continue;

			string trimmed = kind.Trim();
			if (!Accepted.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
			{
				throw new FormatException(
					$"Parameter 'kinds' has unknown kind '{trimmed}'. Accepted kinds (case-insensitive): {string.Join(", ", Accepted)}.");
			}

			wanted.Add(trimmed);
		}

		return wanted.Count > 0 ? wanted : null;
	}

	/// <summary>True when the symbol's outline kind is one of the requested kinds, or no filter was given.</summary>
	public static bool Matches(ISymbol symbol, HashSet<string>? kinds) =>
		kinds is null || kinds.Contains(SymbolKindText.Of(symbol));
}
