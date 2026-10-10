using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// A target recognised by its declarations, so a callee bound in another project's compilation (possibly
/// retargeted) still matches. Within one projection <see cref="Solution"/> the syntax trees are shared, so
/// comparing the <see cref="SyntaxTree"/> and <see cref="TextSpan"/> of the declaring references is exact.
/// Partial members match through either part; a property target also matches its accessors, because a read
/// or write of it is a call to it.
/// </summary>
internal sealed class TargetIdentity
{
	private readonly string Name;
	private readonly bool TargetIsProperty;
	private readonly HashSet<(SyntaxTree Tree, TextSpan Span)> Declarations = new();

	private TargetIdentity(string name, bool targetIsProperty)
	{
		Name = name;
		TargetIsProperty = targetIsProperty;
	}

	public static TargetIdentity Of(ISymbol target)
	{
		var identity = new TargetIdentity(target.Name, targetIsProperty: target is IPropertySymbol);

		identity.Add(target);
		if (target is IPropertySymbol property)
		{
			identity.Add(property.GetMethod);
			identity.Add(property.SetMethod);
		}

		return identity;
	}

	private void Add(ISymbol? symbol)
	{
		foreach (SyntaxReference reference in symbol?.DeclaringSyntaxReferences ?? [])
			Declarations.Add((reference.SyntaxTree, reference.Span));
	}

	public bool Matches(ISymbol callee)
	{
		// A property read or write binds to an accessor; the call is to the property it belongs to.
		ISymbol candidate = TargetIsProperty && callee is IMethodSymbol { AssociatedSymbol: IPropertySymbol property }
			? property
			: callee;

		return string.Equals(candidate.Name, Name, StringComparison.Ordinal)
			&& candidate.DeclaringSyntaxReferences.Any(reference => Declarations.Contains((reference.SyntaxTree, reference.Span)));
	}
}
