using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

/// <summary>
/// The resolved symbol's headline details. When the name resolves to more than one symbol,
/// <see cref="ResultBase.Error"/> carries an <see cref="ErrorCode.Ambiguous"/> whose candidates are the
/// fully-qualified names to disambiguate with; when nothing resolves it carries an
/// <see cref="ErrorCode.NotFound"/> whose candidates are the fuzzy near-misses.
/// </summary>
public sealed record GetSymbolResult : ResultBase
{
	public GetSymbolResult(SolutionModel solutionModel, Error? error, SymbolDto? symbol)
		: base(solutionModel, error)
	{
		Symbol = symbol;
	}

	public SymbolDto? Symbol { get; }
}
