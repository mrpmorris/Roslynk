using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetMembers;

[McpServerToolType]
public sealed class GetMembersTool
{
	public const string GetMembersName = "get_members";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetMembersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = GetMembersName,
		Title = "Get a type's members",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Lists a type's members (methods, properties, fields, events) with kind, accessibility, and
		signature, resolved by fully-qualified name. Private members and inherited members are excluded
		unless requested. Narrow a large type with nameFilter (a trailing '*' matches by prefix, otherwise
		a case-insensitive substring) and the include* kind toggles; these compose with includePrivate and
		includeInherited. Each member carries its source location (sourcePath with 1-based startLine and
		endLine); to read a member's source code, open sourcePath and read startLine through endLine with the
		file tool. Ambiguous names return candidate fully-qualified names instead. Prefer this over reading
		the .cs file to see what a type contains; it is the compiler's view, correct across partial classes
		and (with includeInherited) base types.
		""")]
	public async Task<GetMembersResult> GetMembers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName,
		[Description("Include private members. Default false.")] bool includePrivate = false,
		[Description("Include members inherited from base types. Default false.")] bool includeInherited = false,
		[Description("Optional case-insensitive filter on member name: a trailing '*' matches by prefix (e.g. 'Search*'), otherwise it is a substring match. Default null (no name filtering).")] string? nameFilter = null,
		[Description("Include method members. Default true.")] bool includeMethods = true,
		[Description("Include field members. Default true.")] bool includeFields = true,
		[Description("Include property members. Default true.")] bool includeProperties = true,
		[Description("Include event members. Default true.")] bool includeEvents = true,
		[Description("Include nested type members. Default true.")] bool includeNestedTypes = true)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetMembersResult Success(string resolvedType, IReadOnlyList<MemberDto> members) =>
			new(model.SnapshotId, model.Status, error: null, resolvedType, members);

		GetMembersResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, resolvedType: null, members: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		List<INamedTypeSymbol> types = (await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, typeName))
			.OfType<INamedTypeSymbol>()
			.ToList();

		if (types.Count == 0)
			return Failure(Error.NotFound($"No type matched '{typeName}'."));
		if (types.Count > 1)
		{
			string[] candidates = types.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{typeName}' matched multiple types.", candidates));
		}

		INamedTypeSymbol type = types[0];

		bool NameMatches(string name)
		{
			if (string.IsNullOrEmpty(nameFilter))
				return true;
			if (nameFilter.EndsWith('*'))
				return name.StartsWith(nameFilter[..^1], StringComparison.OrdinalIgnoreCase);

			return name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
		}

		bool KindIncluded(ISymbol member) =>
			member.Kind switch
			{
				SymbolKind.Method => includeMethods,
				SymbolKind.Field => includeFields,
				SymbolKind.Property => includeProperties,
				SymbolKind.Event => includeEvents,
				SymbolKind.NamedType => includeNestedTypes,
				_ => true,
			};

		MemberDto[] members = Collect(type, includeInherited)
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => includePrivate || member.DeclaredAccessibility != Accessibility.Private)
			.Where(KindIncluded)
			.Where(member => NameMatches(member.Name))
			.Select(Map)
			.ToArray();

		return Success(SymbolResolver.FullyQualifiedName(type), members);
	}

	private static MemberDto Map(ISymbol member)
	{
		SyntaxReference? reference = member.DeclaringSyntaxReferences.FirstOrDefault();
		FileLinePositionSpan? span = reference is null
			? null
			: reference.SyntaxTree.GetLineSpan(reference.Span);

		return new MemberDto(
			name: member.Name,
			kind: member.Kind.ToString(),
			accessibility: member.DeclaredAccessibility.ToString(),
			signature: member.ToDisplayString(),
			sourcePath: span?.Path,
			startLine: span is { } start ? start.StartLinePosition.Line + 1 : null,
			endLine: span is { } end ? end.EndLinePosition.Line + 1 : null);
	}

	private static IEnumerable<ISymbol> Collect(INamedTypeSymbol type, bool includeInherited)
	{
		for (INamedTypeSymbol? current = type; current is not null; current = includeInherited ? current.BaseType : null)
		{
			foreach (ISymbol member in current.GetMembers())
				yield return member;
		}
	}
}
