using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Infrastructure.Projections;

/// <summary>
/// One entry per declaration across projections, identified like <see cref="SymbolIdentityIndex"/>, keeping
/// the copy whose signature binds best (<see cref="ErrorTypeCount"/>) and, on a tie, the first one added. A
/// later, better-bound copy replaces the earlier one in place, so entries keep the order their declarations
/// were first seen. Instances are not thread-safe and are meant to live for one query.
/// </summary>
/// <typeparam name="T">A per-copy value carried alongside the symbol, such as the solution it came from.</typeparam>
internal sealed class DeclarationCopies<T>
{
	private readonly SymbolIdentityIndex Identities = new();
	private readonly List<(ISymbol Symbol, T Value, int Errors)> Entries = [];

	public int Count => Entries.Count;

	/// <summary>Each declaration's best-bound copy with its value, in first-seen order.</summary>
	public IEnumerable<(ISymbol Symbol, T Value)> Items => Entries.Select(entry => (entry.Symbol, entry.Value));

	/// <summary>True when <paramref name="symbol"/> is the first copy of its declaration.</summary>
	public bool Add(ISymbol symbol, T value)
	{
		int group = Identities.GroupOf(symbol, out bool isNew);
		if (isNew)
		{
			// SymbolIdentityIndex numbers groups in creation order, so a new group is the next entry.
			Entries.Add((symbol, value, ErrorTypeCount.Of(symbol)));
			return true;
		}

		int kept = Entries[group].Errors;
		if (kept > 0)
		{
			int errors = ErrorTypeCount.Of(symbol);
			if (errors < kept)
				Entries[group] = (symbol, value, errors);
		}

		return false;
	}
}
