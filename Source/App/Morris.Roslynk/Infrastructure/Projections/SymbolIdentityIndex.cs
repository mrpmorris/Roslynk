using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Projections;

/// <summary>
/// Assigns each symbol a group number so that the copies of one declaration found in several projections
/// (one per target framework, or base plus a toggled <c>#if</c> variant) share it.
/// </summary>
/// <remarks>
/// A symbol joins an existing group when its declaration location (kind, file and span) or its
/// <see cref="ProjectionService.KeyOf"/> signature matches one already seen. The location catches a parameter
/// type that failed to bind in one target framework, which renders under a different display string there;
/// the signature keeps merging a member whose declaration moved between <c>#if</c> branches and symbols with no
/// source location. Instances are not thread-safe and are meant to live for one query.
/// </remarks>
internal sealed partial class SymbolIdentityIndex
{
	private readonly Dictionary<string, int> Groups = new(StringComparer.Ordinal);
	private int Count;

	/// <summary>Returns the group <paramref name="symbol"/> belongs to, creating one when it matches none.</summary>
	public int GroupOf(ISymbol symbol, out bool isNew)
	{
		string signature = $"signature:{ProjectionService.KeyOf(symbol)}";
		string? location = LocationKeyOf(symbol);

		if (!Groups.TryGetValue(signature, out int group) && (location is null || !Groups.TryGetValue(location, out group)))
		{
			group = Count++;
			isNew = true;
		}
		else
		{
			isNew = false;
		}

		Groups.TryAdd(signature, group);
		if (location is not null)
			Groups.TryAdd(location, group);

		return group;
	}

	/// <summary>True the first time a declaration is seen, false for any further copy of it.</summary>
	public bool Add(ISymbol symbol)
	{
		GroupOf(symbol, out bool isNew);
		return isNew;
	}

	/// <summary>
	/// Kind, file and span of the symbol's first declaration, or null when it has none in source or is
	/// implicitly declared (a record's synthesized members share the span of the record itself).
	/// </summary>
	private static string? LocationKeyOf(ISymbol symbol)
	{
		if (symbol.IsImplicitlyDeclared || symbol.DeclaringSyntaxReferences.FirstOrDefault() is not SyntaxReference reference)
			return null;

		string path = GeneratedConfigurationFolders().Replace(reference.SyntaxTree.FilePath, "/obj/").Replace('\\', '/');
		string methodKind = symbol is IMethodSymbol method ? method.MethodKind.ToString() : string.Empty;
		return $"location:{symbol.Kind}:{methodKind}:{path}:{reference.Span.Start}:{reference.Span.Length}";
	}

	/// <summary>
	/// <c>obj/&lt;configuration&gt;/&lt;framework&gt;/</c>: generated files of a multi-targeted project differ only
	/// by this part of their path.
	/// </summary>
	[GeneratedRegex(@"[\\/]obj[\\/][^\\/]+[\\/][^\\/]+[\\/]")]
	private static partial Regex GeneratedConfigurationFolders();
}
