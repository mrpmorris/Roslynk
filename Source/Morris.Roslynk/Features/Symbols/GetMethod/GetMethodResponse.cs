namespace Morris.Roslynk.Features.Symbols.GetMethod;

/// <summary>
/// The methods matching a name — one entry per overload. When the name resolves only to non-method
/// symbols, <c>Methods</c> is empty and <c>Candidates</c> lists those fully-qualified names instead.
/// </summary>
public sealed record GetMethodResponse(
	IReadOnlyList<MethodDto> Methods,
	IReadOnlyList<string> Candidates);
