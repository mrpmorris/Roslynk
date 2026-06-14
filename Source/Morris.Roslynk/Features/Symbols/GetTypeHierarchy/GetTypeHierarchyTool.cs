using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

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
	public async Task<GetTypeHierarchyResult> GetTypeHierarchy(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetTypeHierarchyResult Success(string resolvedType, IReadOnlyList<string> baseTypes, IReadOnlyList<string> interfaces, IReadOnlyList<string> derivedTypes) =>
			new(model, error: null, resolvedType, baseTypes, interfaces, derivedTypes);

		GetTypeHierarchyResult Failure(Error error) =>
			new(model, error, resolvedType: null, baseTypes: null, interfaces: null, derivedTypes: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(model.Solution, typeName);
		List<INamedTypeSymbol> types = matches.OfType<INamedTypeSymbol>().ToList();

		if (types.Count == 0)
		{
			string[] resolved = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			IReadOnlyList<string> candidates = resolved.Length > 0
				? resolved
				: await SymbolResolver.SuggestAsync(model.Solution, typeName);

			return Failure(Error.NotFound($"No type matched '{typeName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (types.Count > 1)
		{
			string[] candidates = types.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{typeName}' matched several types.", candidates));
		}

		INamedTypeSymbol type = types[0];

		var baseTypes = new List<string>();
		for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
			baseTypes.Add(SymbolResolver.FullyQualifiedName(baseType));

		string[] interfaces = type.AllInterfaces.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		IEnumerable<INamedTypeSymbol> derived = await SymbolFinder.FindDerivedClassesAsync(type, model.Solution);
		string[] derivedTypes = derived.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		return Success(SymbolResolver.FullyQualifiedName(type), baseTypes, interfaces, derivedTypes);
	}
}
