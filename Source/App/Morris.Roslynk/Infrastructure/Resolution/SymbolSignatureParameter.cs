using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// One parameter parsed out of a caller-supplied signature-qualified name: the type as written (parameter
/// name, default value and <c>params</c>/<c>this</c>/<c>scoped</c> already stripped) plus the ref modifier
/// if one was written. <see cref="RefKind"/> is null when the caller wrote no modifier, which matches a
/// parameter of any <see cref="Microsoft.CodeAnalysis.RefKind"/>.
/// </summary>
public sealed class SymbolSignatureParameter
{
	public string TypeText { get; }
	public RefKind? RefKind { get; }

	public SymbolSignatureParameter(string typeText, RefKind? refKind)
	{
		TypeText = typeText ?? throw new ArgumentNullException(nameof(typeText));
		RefKind = refKind;
	}
}
