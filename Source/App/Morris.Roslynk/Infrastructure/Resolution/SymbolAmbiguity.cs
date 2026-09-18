using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// Builds the one ambiguous-match failure every name-based tool returns, so each tool's candidates are
/// distinguishable and are exactly the strings its own resolver accepts. Lives here rather than on
/// <see cref="Error"/> so the results layer stays free of Roslyn.
/// </summary>
public static class SymbolAmbiguity
{
	/// <summary>
	/// The <see cref="ErrorCode.Ambiguous"/> failure for <paramref name="requestedName"/>, carrying one
	/// candidate per distinct match. Sending a candidate back verbatim resolves to that one symbol.
	/// </summary>
	public static Error Ambiguous(string requestedName, IEnumerable<ISymbol> matches)
	{
		ArgumentNullException.ThrowIfNull(matches);

		IReadOnlyList<string> candidates = SymbolSignature.Distinguish(matches);
		return Error.Ambiguous(
			$"'{requestedName}' matched {candidates.Count} symbols. Retry with one of the candidate names below, exactly as written.",
			candidates);
	}
}
