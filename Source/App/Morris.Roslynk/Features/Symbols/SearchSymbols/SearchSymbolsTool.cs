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

		Searches source symbol names, including generated declarations
		and local functions nested under their declaring member/outer local functions.
		A dotted query is container-qualified: the last segment matches a symbol name and the preceding
		segments must be a whole-segment suffix of its containing type/namespace chain
		(Widget.Compute, SimpleLibrary.Widget.Compute). A bare name stays a case-insensitive substring search.
		Body: project > folder > file > namespace > type > member. Members nest under parent types;
		type locations appear only when the type itself matches.
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
		[Description("Case-insensitive name substring. A dotted query is a container-qualified name: the last segment names the symbol, the preceding segments a suffix of its containing type/namespace chain.")] string query,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();
		return await SearchSymbolsCoreAsync(model, instance, query, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> SearchSymbolsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string query,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// A dotted query is container-qualified rather than a substring of a bare name: the declaration
		// search is keyed on the leaf, and each candidate is then held to the whole-segment suffix of its
		// containing type/namespace chain. A bare name keeps the substring behaviour it has always had.
		SymbolSignatureQuery? containerQuery = null;
		if (SymbolSignature.TryParse(query, out SymbolSignatureQuery parsed) && parsed.Qualified)
			containerQuery = parsed;

		Func<string, bool> predicate = containerQuery is null
			? name => name.Contains(query, StringComparison.OrdinalIgnoreCase)
			: name => string.Equals(name, containerQuery.SimpleName, StringComparison.OrdinalIgnoreCase);

		bool Matches(ISymbol symbol) =>
			containerQuery is null || SymbolSignature.ContainerQualifiedMatches(symbol, containerQuery);

		// Search every projection so declarations that compile only in a branch inactive in the loaded
		// configuration are included; dedupe by fully-qualified name across projections, keeping the
		// best-bound copy of each declaration so a degraded rendering never wins.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution, token);
		var matched = new DeclarationCopies<Solution>();
		foreach (Projection projection in projections)
		{
			foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(projection.Solution, predicate, token))
			{
				if (Matches(symbol))
					matched.Add(symbol, projection.Solution);
			}

			// Roslyn's declaration search covers source-generated documents only when a hand-written
			// declaration in the same project also matches, so the generated ones are asked for directly.
			foreach (ISymbol symbol in await SourceGeneratedDeclarations.FindAsync(projection.Solution, predicate, token))
			{
				if (Matches(symbol))
					matched.Add(symbol, projection.Solution);
			}

			// Local functions are not in the declaration index, so they are found by walking the syntax
			// (source-generated documents included).
			foreach (IMethodSymbol local in await LocalFunctions.FindAllAsync(projection.Solution, predicate, token))
			{
				if (Matches(local))
					matched.Add(local, projection.Solution);
			}
		}

		// The result set is every match; the response budget and maxSize bound what one page carries, so a
		// long result pages with startItem instead of losing matches to a cap. count= names the set's size.
		IReadOnlyList<(ISymbol Symbol, Solution Solution)> page = matched.Items.ToList();

		var root = new SymbolNode();
		foreach ((ISymbol symbol, Solution symbolSolution) in page)
			SymbolPlacement.Place(root, symbol, symbolSolution, solutionDirectory);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => { },
			capHeaders: null,
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
