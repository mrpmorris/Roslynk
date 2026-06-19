namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// One step in an enclosing-declaration path: a kind (lower-cased TypeKind for a type, else lower-cased
/// SymbolKind) and the declaration's name, e.g. ("class", "OrderService") or ("method", "Checkout").
/// </summary>
public sealed class EnclosingSegment
{
	public string Kind { get; }
	public string Name { get; }

	public EnclosingSegment(string kind, string name)
	{
		Kind = kind;
		Name = name;
	}
}
