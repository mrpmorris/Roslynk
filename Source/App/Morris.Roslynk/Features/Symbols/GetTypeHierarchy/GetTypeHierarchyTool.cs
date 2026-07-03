using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.GetTypeHierarchy;

[McpServerToolType]
public sealed class GetTypeHierarchyTool
{
	public const string GetTypeHierarchyName = "get_type_hierarchy";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public GetTypeHierarchyTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
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
		fully-qualified name.
		{OutlineDescriptions.CommonMethodInstructions}
		The body has up to three sections (base, interfaces, derived); an empty section is omitted, and
		each entry is a '<typeKind>,<fully-qualified name>':
		  resolvedType=<fully-qualified type>

		  base
		  \t<typeKind>,<fully-qualified name>
		  interfaces
		  \t<typeKind>,<fully-qualified name>
		  derived
		  \t<typeKind>,<fully-qualified name>
		where typeKind is class|struct|interface|enum|delegate; {OutlineDescriptions.ListFieldQuoting}. {OutlineDescriptions.ErrorBlock} Prefer this
		over reading files to reconstruct a hierarchy; the chain comes from the compiler, including base types
		defined in referenced assemblies.
		""")]
	public async Task<string> GetTypeHierarchy(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, typeName);
		List<IReadOnlyList<ProjectionSymbol>> typeGroups = groups.Where(group => group[0].Symbol is INamedTypeSymbol).ToList();

		if (typeGroups.Count == 0)
		{
			string[] resolved = groups.Select(group => SymbolResolver.FullyQualifiedName(group[0].Symbol)).Distinct(StringComparer.Ordinal).ToArray();
			IReadOnlyList<string> candidates = resolved.Length > 0
				? resolved
				: await SymbolResolver.SuggestAsync(model.Solution, typeName);

			return Failure(Error.NotFound($"No type matched '{typeName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (typeGroups.Count > 1)
		{
			string[] candidates = typeGroups.Select(group => SymbolResolver.FullyQualifiedName(group[0].Symbol)).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{typeName}' matched several types.", candidates));
		}

		IReadOnlyList<ProjectionSymbol> resolvedType = typeGroups[0];
		var type = (INamedTypeSymbol)resolvedType[0].Symbol;

		var baseTypes = new List<INamedTypeSymbol>();
		for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
			baseTypes.Add(baseType);

		List<INamedTypeSymbol> interfaces = type.AllInterfaces
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.OrderBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		// Union derived types across every projection so a derived type that compiles only in a branch
		// inactive in the loaded configuration is still listed.
		var seenDerived = new HashSet<string>(StringComparer.Ordinal);
		var derivedList = new List<INamedTypeSymbol>();
		foreach (ProjectionSymbol projectionSymbol in resolvedType)
		{
			foreach (INamedTypeSymbol derivedType in await SymbolFinder.FindDerivedClassesAsync((INamedTypeSymbol)projectionSymbol.Symbol, projectionSymbol.Projection.Solution))
			{
				if (seenDerived.Add(SymbolResolver.FullyQualifiedName(derivedType)))
					derivedList.Add(derivedType);
			}
		}

		List<INamedTypeSymbol> derived = derivedList
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
		if (types.Count == 0)
			return;

		builder.Line(0, title);
		foreach (INamedTypeSymbol type in types)
			builder.Line(1, $"{SymbolKindText.Of(type)},{OutlineBuilder.Field(SymbolResolver.FullyQualifiedName(type))}");
	}
}
