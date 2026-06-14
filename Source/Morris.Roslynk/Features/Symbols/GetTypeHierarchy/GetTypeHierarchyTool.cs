using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.Symbols.GetTypeHierarchy;

[McpServerToolType]
public sealed class GetTypeHierarchyTool
{
	public const string GetTypeHierarchyName = "get_type_hierarchy";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetTypeHierarchyTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = GetTypeHierarchyName,
		Title = "Get a type's hierarchy",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Returns a type's base-type chain, implemented interfaces, and known derived types, resolved by
		fully-qualified name. Ambiguous names return candidate fully-qualified names instead.
		""")]
	public async Task<TypeHierarchyResponse> GetTypeHierarchy(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		Solution solution = instance.CurrentSolution;

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, typeName);
		List<INamedTypeSymbol> types = matches.OfType<INamedTypeSymbol>().ToList();

		if (types.Count == 0)
			return new TypeHierarchyResponse(null, [], [], [], []);
		if (types.Count > 1)
			return new TypeHierarchyResponse(null, [], [], [], types.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray());

		INamedTypeSymbol type = types[0];

		var baseTypes = new List<string>();
		for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
			baseTypes.Add(SymbolResolver.FullyQualifiedName(baseType));

		string[] interfaces = type.AllInterfaces.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, solution);
		string[] derivedTypes = derived.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		return new TypeHierarchyResponse(SymbolResolver.FullyQualifiedName(type), baseTypes, interfaces, derivedTypes, []);
	}
}
