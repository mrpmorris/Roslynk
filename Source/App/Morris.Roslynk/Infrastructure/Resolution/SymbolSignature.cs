using System.Text;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Resolution;

/// <summary>
/// The one place a symbol's name is rendered for a caller and parsed back from one, so the strings a tool
/// emits are exactly the strings resolution accepts. A name is the fully-qualified name plus, for a method
/// or indexer, a parameter-type list: <c>N.T.M(int, string)</c> or <c>N.T.this[int]</c>. A name written
/// without a list stays parameter-agnostic and matches every overload.
/// </summary>
/// <remarks>
/// Parameter types render at escalating <see cref="SignatureTier"/>s; <see cref="Distinguish"/> picks the
/// lowest tier that tells a set of symbols apart and <see cref="Matches"/> accepts all of them. Parameter
/// names, default values, <c>params</c>/<c>this</c>/<c>scoped</c> and whitespace are never emitted and are
/// ignored on input. Nullable reference annotations are never emitted and never affect matching, while
/// <c>int?</c> stays distinct from <c>int</c> because the two are genuinely different overloads.
/// </remarks>
public static class SymbolSignature
{
	private static readonly SymbolDisplayFormat MinimalParameterFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	private static readonly SymbolDisplayFormat FullyQualifiedParameterFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

	// The annotated variants exist only so a caller who pasted an annotated type (find_references used to
	// print ToDisplayString()) is still understood; nothing emits them.
	private static readonly SymbolDisplayFormat MinimalAnnotatedParameterFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
			| SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	private static readonly SymbolDisplayFormat FullyQualifiedAnnotatedParameterFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	/// <summary>A local function's own name segment, with any type-parameter list: <c>local</c> or <c>local&lt;T&gt;</c>.</summary>
	private static readonly SymbolDisplayFormat LocalNameFormat = new(
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

	private static readonly SignatureTier[] Tiers =
	[
		SignatureTier.Minimal,
		SignatureTier.MinimalWithRefKinds,
		SignatureTier.FullyQualified,
		SignatureTier.FullyQualifiedWithRefKinds
	];

	private static readonly string[] ParameterModifiers =
		["ref", "out", "in", "params", "this", "scoped", "readonly"];

	private const string GlobalPrefix = "global::";

	/// <summary>
	/// The symbol's name as a caller should write it: the fully-qualified name, plus a parameter-type list
	/// for a method or indexer so overloads are distinguishable.
	/// </summary>
	public static string Of(ISymbol symbol, SignatureTier tier = SignatureTier.Minimal)
	{
		ArgumentNullException.ThrowIfNull(symbol);

		// A local function carries its container's full signature, so the name stays unique when the container
		// is overloaded (N.T.M(int).local(string) vs N.T.M(string).local(string)) and round-trips as a candidate.
		if (symbol is IMethodSymbol local && LocalFunctions.IsLocalFunction(local))
			return $"{Of(LocalFunctions.NamedContainer(local), tier)}.{local.ToDisplayString(LocalNameFormat)}({RenderParameters(local.Parameters, tier)})";

		string head = SymbolResolver.FullyQualifiedName(symbol);
		return symbol switch
		{
			IMethodSymbol method => $"{head}({RenderParameters(method.Parameters, tier)})",
			IPropertySymbol { IsIndexer: true } indexer => $"{head}[{RenderParameters(indexer.Parameters, tier)}]",
			_ => head
		};
	}

	/// <summary>
	/// Renders every symbol at the lowest <see cref="SignatureTier"/> that tells the set apart, deduped and
	/// ordinal-ordered. Two symbols that render the same at every tier are the same member reached through
	/// several projects, and are deliberately reported once.
	/// </summary>
	public static IReadOnlyList<string> Distinguish(IEnumerable<ISymbol> symbols)
	{
		ArgumentNullException.ThrowIfNull(symbols);

		ISymbol[] candidates = symbols.ToArray();
		if (candidates.Length == 0)
			return [];

		string[] best = [];
		int bestDistinct = 0;
		foreach (SignatureTier tier in Tiers)
		{
			string[] rendered = candidates.Select(candidate => Of(candidate, tier)).ToArray();
			int distinct = rendered.Distinct(StringComparer.Ordinal).Count();
			if (distinct > bestDistinct)
			{
				best = rendered;
				bestDistinct = distinct;
			}

			if (distinct == candidates.Length)
				break;
		}

		return best
			.Distinct(StringComparer.Ordinal)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
	}

	/// <summary>
	/// Narrows a modifier-agnostic query's matches to the overloads that take their parameters by value.
	/// A name written without <c>ref</c>/<c>out</c>/<c>in</c> matches any ref kind, which keeps a caller who
	/// does not know the modifier from hitting a dead end — but it would also make the candidate emitted for
	/// a by-value overload ambiguous against its by-ref sibling. Preferring the by-value overloads when both
	/// were matched keeps the candidate exact while leaving the lenient behaviour in place for a member that
	/// only exists by ref.
	/// </summary>
	public static IReadOnlyList<ISymbol> Narrow(IReadOnlyList<ISymbol> matches, SymbolSignatureQuery query)
	{
		ArgumentNullException.ThrowIfNull(matches);
		ArgumentNullException.ThrowIfNull(query);

		if (matches.Count < 2 || query.Parameters.Any(parameter => parameter.RefKind is not null))
			return matches;

		List<ISymbol> byValue = matches.Where(TakesEveryParameterByValue).ToList();
		return byValue.Count > 0 && byValue.Count < matches.Count ? byValue : matches;
	}

	private static bool TakesEveryParameterByValue(ISymbol symbol)
	{
		IEnumerable<IParameterSymbol> parameters = symbol switch
		{
			IMethodSymbol method => method.Parameters,
			IPropertySymbol { IsIndexer: true } indexer => indexer.Parameters,
			_ => []
		};

		return parameters.All(parameter => parameter.RefKind == RefKind.None);
	}

	/// <summary>
	/// Splits a caller-supplied name into its resolvable parts. Returns false only for a blank name or an
	/// unbalanced parameter list; anything else parses, because an unmatched name is a resolution miss
	/// rather than a parse failure.
	/// </summary>
	public static bool TryParse(string? name, out SymbolSignatureQuery query)
	{
		query = null!;
		if (string.IsNullOrWhiteSpace(name))
			return false;

		string text = name.Trim();
		if (text.StartsWith(GlobalPrefix, StringComparison.Ordinal))
			text = text[GlobalPrefix.Length..];

		var listKind = ParameterListKind.None;
		string head = text;
		string inner = "";
		char last = text[^1];
		if (last is ')' or ']')
		{
			int open = IndexOfMatchingOpen(text, text.Length - 1);
			if (open < 0)
				return false;

			inner = text[(open + 1)..^1];
			head = text[..open].TrimEnd();
			listKind = last == ')' ? ParameterListKind.Parentheses : ParameterListKind.Brackets;
		}

		if (head.Length == 0)
			return false;

		int lastDot = LastTopLevelDot(head);
		string segment = lastDot >= 0 ? head[(lastDot + 1)..] : head;
		(string simpleName, int arity) = SplitArity(segment);
		if (simpleName.Length == 0)
			return false;

		query = new SymbolSignatureQuery(
			qualifiedName: head,
			simpleName: simpleName,
			arity: arity,
			qualified: lastDot >= 0,
			listKind: listKind,
			parameters: ParseParameters(inner));
		return true;
	}

	/// <summary>
	/// The part of a qualified query before its last segment, parameter lists included: <c>N.T.M(int)</c> for
	/// <c>N.T.M(int).local(string)</c>. False for an unqualified query.
	/// </summary>
	public static bool TryGetContainer(SymbolSignatureQuery query, out string container)
	{
		ArgumentNullException.ThrowIfNull(query);

		int lastDot = LastTopLevelDot(query.QualifiedName);
		container = lastDot > 0 ? query.QualifiedName[..lastDot].TrimEnd() : "";
		return container.Length > 0;
	}

	/// <summary>
	/// Whether the symbol is what the parsed name asked for. A query with no parameter list matches a member
	/// of any signature; a query with one matches only a method or indexer whose parameters line up.
	/// </summary>
	public static bool Matches(ISymbol symbol, SymbolSignatureQuery query)
	{
		ArgumentNullException.ThrowIfNull(symbol);
		ArgumentNullException.ThrowIfNull(query);

		return symbol switch
		{
			IMethodSymbol method => MethodMatches(method, query),
			IPropertySymbol { IsIndexer: true } indexer =>
				NameMatches(indexer, query, allowStrippedArity: false)
				&& (query.ListKind == ParameterListKind.None
					|| (query.ListKind == ParameterListKind.Brackets && ParametersMatch(indexer.Parameters, query.Parameters))),
			_ => query.ListKind == ParameterListKind.None && NameMatches(symbol, query, allowStrippedArity: false)
		};
	}

	private static bool MethodMatches(IMethodSymbol method, SymbolSignatureQuery query)
	{
		if (!NameMatches(method, query, allowStrippedArity: true))
			return false;

		if (query.Arity >= 0 && method.Arity != query.Arity)
			return false;

		return query.ListKind switch
		{
			ParameterListKind.None => true,
			ParameterListKind.Parentheses => ParametersMatch(method.Parameters, query.Parameters),
			_ => false
		};
	}

	/// <summary>
	/// Compares the name itself. An unqualified query matches on the simple name, preserving the behaviour a
	/// bare name has always had. A generic method may also be written without its type-parameter list —
	/// <c>N.T.Get</c> for <c>N.T.Get&lt;T&gt;</c> — because a caller has no way to know the declared name.
	/// </summary>
	private static bool NameMatches(ISymbol symbol, SymbolSignatureQuery query, bool allowStrippedArity)
	{
		if (!query.Qualified)
			return string.Equals(symbol.Name, query.SimpleName, StringComparison.Ordinal);

		// A local function's container segment may carry its own parameter list (N.T.M(int).local), so the
		// container is matched as a name in its own right rather than by comparing flattened text.
		if (symbol is IMethodSymbol local && LocalFunctions.IsLocalFunction(local))
		{
			return string.Equals(local.Name, query.SimpleName, StringComparison.Ordinal)
				&& TryGetContainer(query, out string container)
				&& TryParse(container, out SymbolSignatureQuery containerQuery)
				&& Matches(LocalFunctions.NamedContainer(local), containerQuery);
		}

		string head = SymbolResolver.FullyQualifiedName(symbol);
		if (string.Equals(head, query.QualifiedName, StringComparison.Ordinal))
			return true;

		return allowStrippedArity
			&& query.Arity < 0
			&& string.Equals(StripTrailingArity(head), query.QualifiedName, StringComparison.Ordinal);
	}

	private static bool ParametersMatch(
		IReadOnlyList<IParameterSymbol> actual,
		IReadOnlyList<SymbolSignatureParameter> requested)
	{
		if (actual.Count != requested.Count)
			return false;

		for (int index = 0; index < actual.Count; index++)
		{
			if (!ParameterMatches(actual[index], requested[index]))
				return false;
		}

		return true;
	}

	private static bool ParameterMatches(IParameterSymbol actual, SymbolSignatureParameter requested)
	{
		if (requested.RefKind is RefKind refKind && !RefKindMatches(actual.RefKind, refKind))
			return false;

		string text = Condense(requested.TypeText);
		foreach (string accepted in AcceptedTypeTexts(actual.Type))
		{
			if (string.Equals(Condense(accepted), text, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	/// <summary>
	/// <c>in</c> and <c>ref readonly</c> are the same thing to a caller, so either spelling matches either
	/// declaration. Everything else must match exactly, because ref kind genuinely distinguishes overloads.
	/// </summary>
	private static bool RefKindMatches(RefKind actual, RefKind requested) =>
		Normalize(actual) == Normalize(requested);

	private static RefKind Normalize(RefKind refKind) =>
		refKind == RefKind.RefReadOnlyParameter ? RefKind.In : refKind;

	/// <summary>
	/// Every spelling of a parameter's type that resolution honours: minimally qualified and fully
	/// qualified, each with and without nullable reference annotations, plus the long form of a nullable
	/// value type.
	/// </summary>
	private static IEnumerable<string> AcceptedTypeTexts(ITypeSymbol type)
	{
		yield return type.ToDisplayString(MinimalParameterFormat);
		yield return type.ToDisplayString(FullyQualifiedParameterFormat);
		yield return type.ToDisplayString(MinimalAnnotatedParameterFormat);
		yield return type.ToDisplayString(FullyQualifiedAnnotatedParameterFormat);

		if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
			&& nullable.TypeArguments.Length == 1)
		{
			ITypeSymbol argument = nullable.TypeArguments[0];
			yield return $"Nullable<{argument.ToDisplayString(MinimalParameterFormat)}>";
			yield return $"System.Nullable<{argument.ToDisplayString(FullyQualifiedParameterFormat)}>";
		}
	}

	private static string RenderParameters(IReadOnlyList<IParameterSymbol> parameters, SignatureTier tier) =>
		string.Join(", ", parameters.Select(parameter => RenderParameter(parameter, tier)));

	private static string RenderParameter(IParameterSymbol parameter, SignatureTier tier)
	{
		bool fullyQualified = tier is SignatureTier.FullyQualified or SignatureTier.FullyQualifiedWithRefKinds;
		string type = parameter.Type.ToDisplayString(fullyQualified ? FullyQualifiedParameterFormat : MinimalParameterFormat);

		if (tier is not (SignatureTier.MinimalWithRefKinds or SignatureTier.FullyQualifiedWithRefKinds))
			return type;

		string? modifier = Normalize(parameter.RefKind) switch
		{
			RefKind.Ref => "ref",
			RefKind.Out => "out",
			RefKind.In => "in",
			_ => null
		};

		return modifier is null ? type : $"{modifier} {type}";
	}

	private static IReadOnlyList<SymbolSignatureParameter> ParseParameters(string inner)
	{
		if (inner.Trim().Length == 0)
			return [];

		var parameters = new List<SymbolSignatureParameter>();
		foreach (string part in SplitTopLevel(inner))
			parameters.Add(ParseParameter(part));

		return parameters;
	}

	/// <summary>
	/// Reads one written parameter down to the type and its ref modifier, discarding the default value, any
	/// non-ref modifier and the parameter's own name — none of which identify an overload.
	/// </summary>
	private static SymbolSignatureParameter ParseParameter(string text)
	{
		string remaining = text.Trim();

		int equals = IndexOfTopLevel(remaining, '=');
		if (equals >= 0)
			remaining = remaining[..equals].TrimEnd();

		RefKind? refKind = null;
		bool consumed = true;
		while (consumed)
		{
			consumed = false;
			foreach (string modifier in ParameterModifiers)
			{
				if (!StartsWithWord(remaining, modifier))
					continue;

				refKind ??= modifier switch
				{
					"ref" => RefKind.Ref,
					"out" => RefKind.Out,
					"in" => RefKind.In,
					_ => null
				};

				remaining = remaining[modifier.Length..].TrimStart();
				consumed = true;
				break;
			}
		}

		// What is left is the type, optionally followed by the parameter's name; a trailing bare identifier
		// can only be that name, because a type would have ended in a bracket, a '?' or a dotted segment.
		int split = LastTopLevelSpace(remaining);
		if (split >= 0)
		{
			string trailing = remaining[(split + 1)..];
			if (IsPlainIdentifier(trailing))
				remaining = remaining[..split].TrimEnd();
		}

		if (remaining.StartsWith(GlobalPrefix, StringComparison.Ordinal))
			remaining = remaining[GlobalPrefix.Length..];

		return new SymbolSignatureParameter(remaining, refKind);
	}

	private static bool StartsWithWord(string text, string word) =>
		text.StartsWith(word, StringComparison.Ordinal)
		&& text.Length > word.Length
		&& char.IsWhiteSpace(text[word.Length]);

	private static bool IsPlainIdentifier(string text)
	{
		if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_' || text[0] == '@'))
			return false;

		foreach (char character in text)
		{
			if (!char.IsLetterOrDigit(character) && character != '_' && character != '@')
				return false;
		}

		return true;
	}

	private static (string Name, int Arity) SplitArity(string segment)
	{
		if (segment.Length == 0 || segment[^1] != '>')
			return (segment, -1);

		int open = IndexOfMatchingOpen(segment, segment.Length - 1);
		if (open <= 0)
			return (segment, -1);

		string arguments = segment[(open + 1)..^1];
		int arity = arguments.Trim().Length == 0 ? 0 : SplitTopLevel(arguments).Count;
		return (segment[..open], arity);
	}

	private static string StripTrailingArity(string head)
	{
		int lastDot = LastTopLevelDot(head);
		string segment = lastDot >= 0 ? head[(lastDot + 1)..] : head;
		(string name, int arity) = SplitArity(segment);
		return arity < 0 ? head : (lastDot >= 0 ? head[..(lastDot + 1)] + name : name);
	}

	/// <summary>Removes whitespace so spelling a signature with or without spaces resolves alike.</summary>
	private static string Condense(string text)
	{
		var builder = new StringBuilder(text.Length);
		foreach (char character in text)
		{
			if (!char.IsWhiteSpace(character))
				builder.Append(character);
		}

		return builder.ToString();
	}

	/// <summary>
	/// The position of the bracket that opens the one closing at <paramref name="closeIndex"/>, or -1 when
	/// the brackets are unbalanced. Tracks every bracket family so a generic argument inside a parameter
	/// list, or a parameter list inside a generic argument, nests correctly.
	/// </summary>
	private static int IndexOfMatchingOpen(string text, int closeIndex)
	{
		int depth = 0;
		for (int index = closeIndex; index >= 0; index--)
		{
			switch (text[index])
			{
				case ')':
				case ']':
				case '>':
					depth++;
					break;

				case '(':
				case '[':
				case '<':
					depth--;
					if (depth == 0)
						return index;
					if (depth < 0)
						return -1;
					break;
			}
		}

		return -1;
	}

	private static int LastTopLevelDot(string text) => LastTopLevel(text, '.');

	private static int LastTopLevelSpace(string text) => LastTopLevel(text, ' ');

	private static int LastTopLevel(string text, char target)
	{
		int depth = 0;
		for (int index = text.Length - 1; index >= 0; index--)
		{
			char character = text[index];
			if (character is ')' or ']' or '>')
				depth++;
			else if (character is '(' or '[' or '<')
				depth--;
			else if (depth == 0 && character == target)
				return index;
		}

		return -1;
	}

	private static int IndexOfTopLevel(string text, char target)
	{
		int depth = 0;
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			if (character is '(' or '[' or '<')
				depth++;
			else if (character is ')' or ']' or '>')
				depth--;
			else if (depth == 0 && character == target)
				return index;
		}

		return -1;
	}

	private static List<string> SplitTopLevel(string text)
	{
		var parts = new List<string>();
		int depth = 0;
		int start = 0;
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			if (character is '(' or '[' or '<')
			{
				depth++;
			}
			else if (character is ')' or ']' or '>')
			{
				depth--;
			}
			else if (character == ',' && depth == 0)
			{
				parts.Add(text[start..index]);
				start = index + 1;
			}
		}

		parts.Add(text[start..]);
		return parts;
	}
}
