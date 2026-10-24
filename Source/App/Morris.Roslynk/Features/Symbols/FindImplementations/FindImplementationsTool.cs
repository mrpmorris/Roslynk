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
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.FindImplementations;

[McpServerToolType]
public sealed class FindImplementationsTool
{
	public const string FindImplementationsName = "find_implementations";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public FindImplementationsTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds implementations/overrides of interfaces, interface members or abstract members.
		Returns resolvedSymbol (including method/indexer parameter types).
		Body: project > folder > file > namespace; leaves: <typeKind>,<typeName>,<loc>.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = FindImplementationsName,
		Title = "Find implementations",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindImplementations(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Interface/abstract symbol name.")] string symbolName,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, _) =>
				FindImplementationsCoreAsync(
					model: model,
					instance: instance,
					symbolName: symbolName,
					startItem: startItem,
					maxSize: maxSize,
					countOnly: countOnly,
					Budget: Budget
				),
			cancellationToken);
	}

	internal async Task<string> FindImplementationsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		int startItem = 1,
		int maxSize = 0,
		bool countOnly = false,
		ResponseBudget? Budget = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName);
		if (groups.Count == 0)
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'."));
		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(symbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union implementors across every projection (per-TFM + #if-toggle), deduped by stable symbol identity,
		// so implementors declared in a branch that is inactive in the loaded configuration are still listed;
		// the copy kept is the best bound, so the placement always describes a signature that bound.
		var implementors = new DeclarationCopies<Solution>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ISymbol implementation in await SymbolFinder.FindImplementationsAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution))
			{
				implementors.Add(implementation, projectionSymbol.Projection.Solution);
			}
		}

		var root = new SymbolNode();
		foreach ((ISymbol implementation, Solution implementationSolution) in implementors.Items)
			SymbolPlacement.Place(root, implementation, implementationSolution, solutionDirectory);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => builder.Header("resolvedSymbol", SymbolResolver.SignatureName(resolved[0].Symbol)),
			builder => root.Render(builder),
			root.Items(),
			startItem,
			maxSize,
			Budget,
			countOnly: countOnly);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
