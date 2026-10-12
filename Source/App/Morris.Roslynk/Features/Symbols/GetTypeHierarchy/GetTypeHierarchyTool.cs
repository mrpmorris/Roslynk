using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
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
	private readonly ResponseBudget Budget;

	public GetTypeHierarchyTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Returns a type's base-type chain, implemented interfaces and known derived types. The body has up
		to three sections (base, interfaces, derived); an empty section is omitted, and each entry is a
		'<typeKind>,<fully-qualified name>' where typeKind is class|struct|interface|enum|delegate:
		```
		  resolvedType=<fully-qualified type>

		  base
		  \t<typeKind>,<fully-qualified name>
		  interfaces
		  \t<typeKind>,<fully-qualified name>
		  derived
		  \t<typeKind>,<fully-qualified name>
		```
		The chain comes from the compiler, including base types defined in referenced assemblies.
		""")]
	[McpServerTool(
		Name = GetTypeHierarchyName,
		Title = "Get a type's hierarchy",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetTypeHierarchy(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified type name; reuse names returned by discovery. Include parameter types to select one overload.")] string typeName,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetTypeHierarchyCoreAsync(model, instance, typeName, startItem, maxSize, Budget);
	}

	internal async Task<string> GetTypeHierarchyCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string typeName,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(
			SymbolResolver,
			projections,
			typeName,
			includeMetadata: false,
			accept: symbol => symbol is INamedTypeSymbol);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, typeName);
			return Failure(Error.NotFound($"No type matched '{typeName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(typeName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolvedType = groups[0];
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

		// Items for the shared pager: one per entry, its section header as the open container line.
		var items = new List<OutlineItem>();
		void Collect(string title, IReadOnlyList<INamedTypeSymbol> types)
		{
			foreach (INamedTypeSymbol entry in types)
				items.Add(OutlineItem.Single($"{SymbolKindText.Of(entry)},{SymbolResolver.FullyQualifiedName(entry)}", [title]));
		}

		Collect("base", baseTypes);
		Collect("interfaces", interfaces);
		Collect("derived", derived);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => builder.Header("resolvedType", SymbolResolver.SignatureName(type)),
			builder =>
			{
				Section(builder, "base", baseTypes);
				Section(builder, "interfaces", interfaces);
				Section(builder, "derived", derived);
			},
			items,
			startItem,
			maxSize,
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
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
