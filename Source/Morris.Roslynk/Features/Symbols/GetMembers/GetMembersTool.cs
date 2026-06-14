using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

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
		unless requested. Ambiguous names return candidate fully-qualified names instead.
		""")]
	public async Task<GetMembersResponse> GetMembers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName,
		[Description("Include private members. Default false.")] bool includePrivate = false,
		[Description("Include members inherited from base types. Default false.")] bool includeInherited = false)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);

		List<INamedTypeSymbol> types = (await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(instance.CurrentSolution, typeName))
			.OfType<INamedTypeSymbol>()
			.ToList();

		if (types.Count == 0)
			return new GetMembersResponse(null, [], []);
		if (types.Count > 1)
			return new GetMembersResponse(null, [], types.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray());

		INamedTypeSymbol type = types[0];

		MemberDto[] members = Collect(type, includeInherited)
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => includePrivate || member.DeclaredAccessibility != Accessibility.Private)
			.Select(member => new MemberDto(member.Name, member.Kind.ToString(), member.DeclaredAccessibility.ToString(), member.ToDisplayString()))
			.ToArray();

		return new GetMembersResponse(SymbolResolver.FullyQualifiedName(type), members, []);
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
