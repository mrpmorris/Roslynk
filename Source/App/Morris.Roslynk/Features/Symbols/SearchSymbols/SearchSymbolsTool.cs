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

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

[McpServerToolType]
public sealed class SearchSymbolsTool
{
	public const string SearchSymbolsName = "search_symbols";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public SearchSymbolsTool(InstanceRegistry instanceRegistry, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Searches source-declared symbols whose name contains the query (case-insensitive) across the
		solution. Declarations a source generator emits are included, as are local functions nesting
		under the member (and any outer local functions) that declares them. Matches are grouped project
		-> folder -> file -> namespace -> type -> member; a matched member nests under its (parent-only)
		type, and a type's location appears only when the type itself matched.
		""")]
	[McpServerTool(
		Name = SearchSymbolsName,
		Title = "Search symbols by name",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> SearchSymbols(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Substring to match against symbol names (case-insensitive).")] string query,
		[Description("Maximum results to return; omit for every result.")] int? maxResults = null,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await SearchSymbolsCoreAsync(model, instance, query, maxResults, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> SearchSymbolsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string query,
		int? maxResults = null,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Search every projection so declarations that compile only in a branch inactive in the loaded
		// configuration are included; dedupe by fully-qualified name across projections, keeping the
		// best-bound copy of each declaration so a degraded rendering never wins.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution, token);
		var matched = new DeclarationCopies<Solution>();
		Func<string, bool> predicate = name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
		foreach (Projection projection in projections)
		{
			foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(projection.Solution, predicate, token))
			{
				matched.Add(symbol, projection.Solution);
			}

			// Roslyn's declaration search covers source-generated documents only when a hand-written
			// declaration in the same project also matches, so the generated ones are asked for directly.
			foreach (ISymbol symbol in await SourceGeneratedDeclarations.FindAsync(projection.Solution, predicate, token))
			{
				matched.Add(symbol, projection.Solution);
			}

			// Local functions are not in the declaration index, so they are found by walking the syntax
			// (source-generated documents included).
			foreach (IMethodSymbol local in await LocalFunctions.FindAllAsync(projection.Solution, predicate, token))
			{
				matched.Add(local, projection.Solution);
			}
		}

		// The result set: capped only when the caller asked for a cap, so omitting maxResults pages through
		// everything. count= names the size of this set.
		IReadOnlyList<(ISymbol Symbol, Solution Solution)> page = maxResults is int cap
			? matched.Items.Take(Math.Max(0, cap)).ToList()
			: matched.Items.ToList();
		bool capped = matched.Count > page.Count;

		var root = new SymbolNode();
		foreach ((ISymbol symbol, Solution symbolSolution) in page)
			SymbolPlacement.Place(root, symbol, symbolSolution, solutionDirectory);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => { },
			capHeaders: capped
				? builder =>
				{
					builder.Header("count", page.Count);
					builder.Header("truncated", true);
				}
				: null,
			renderBody: builder => root.Render(builder),
			items: root.Items(),
			startItem: startItem,
			maxSize: maxSize,
			budget: budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), model.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
