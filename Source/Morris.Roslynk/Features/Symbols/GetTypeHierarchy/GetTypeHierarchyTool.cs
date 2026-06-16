using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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
		$"""
		Returns a type's base-type chain, implemented interfaces, and known derived types, resolved by
		fully-qualified name. {OutlineDescriptions.TextNotJson} The body has three fixed sections, each entry a
		'<typeKind>,<fully-qualified name>':
		  #resolvedType=<fully-qualified type>
		  #status=Ready

		  base
		  \t<typeKind>,<fully-qualified name>
		  interfaces
		  \t<typeKind>,<fully-qualified name>
		  derived
		  \t<typeKind>,<fully-qualified name>
		where typeKind is class|struct|interface|enum|delegate. {OutlineDescriptions.ErrorBlock} Prefer this
		over reading files to reconstruct a hierarchy; the chain comes from the compiler, including base types
		defined in referenced assemblies.
		""")]
	public async Task<string> GetTypeHierarchy(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

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

		var baseTypes = new List<INamedTypeSymbol>();
		for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
			baseTypes.Add(baseType);

		List<INamedTypeSymbol> interfaces = type.AllInterfaces
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.OrderBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		List<INamedTypeSymbol> derived = (await SymbolFinder.FindDerivedClassesAsync(type, model.Solution))
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.OrderBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		var builder = new OutlineBuilder();
		builder.Header("resolvedType", SymbolResolver.FullyQualifiedName(type));
		builder.Status(model.Status);
		builder.BeginBody();

		Section(builder, "base", baseTypes);
		Section(builder, "interfaces", interfaces);
		Section(builder, "derived", derived);
		return builder.ToString();
	}

	private void Section(OutlineBuilder builder, string title, IReadOnlyList<INamedTypeSymbol> types)
	{
		builder.Line(0, title);
		foreach (INamedTypeSymbol type in types)
			builder.Line(1, $"{SymbolKindText.Of(type)},{SymbolResolver.FullyQualifiedName(type)}");
	}
}
