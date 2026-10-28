using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

[McpServerToolType]
public sealed class SearchSymbolsTool : IMcpTool
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

	public static string GetName() => SearchSymbolsName;

	public static string GetDescription() => """
		SupportsPaging=Y

		Searches source symbol names, including generated declarations
		and local functions nested under their declaring member/outer local functions.
		symbolNameFilter matches a symbol's own name, not free text and not its members: a bare name is a
		case-insensitive substring, and a dotted name is container-qualified - the last segment matches a
		symbol name and the preceding segments must be a whole-segment suffix of its containing
		type/namespace chain (Widget.Compute, SimpleLibrary.Widget.Compute). To list a type's members, use
		get_members; a member cannot be selected by its container alone here.
		Body: project > folder > file > namespace > type > member. Members nest under parent types;
		type locations appear only when the type itself matches.
		'kinds' filters the matched declarations by outline kind (namespace, class, struct, interface, enum,
		delegate, module, method, property, field, event, localfunction), case-insensitively; an unknown kind
		name is error=Invalid rather than an empty result.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput;

	[McpServerTool(
		Name = SearchSymbolsName,
		Title = "Search symbols by name",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> SearchSymbols(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.SymbolNameFilter)] string symbolNameFilter,
		[Description("Optional kind filter: a JSON array of kind names, e.g. [\"Method\"]. A JSON-array string such as \"[\\\"Method\\\"]\" is also accepted. Kinds (case-insensitive): namespace, class, struct, interface, enum, delegate, module, method, property, field, event, localfunction. Omit or pass [] for all kinds.")] string[]? kinds = null,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, _) =>
				SearchSymbolsCoreAsync(
					model: model,
					instance: instance,
					symbolNameFilter: symbolNameFilter,
					kinds: kinds,
					startItem: startItem,
					maxSize: maxSize,
					countOnly: countOnly,
					budget: Budget
				),
			cancellationToken);
	}

	internal async Task<string> SearchSymbolsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolNameFilter,
		string[]? kinds = null,
		int startItem = 1,
		int maxSize = 0,
		bool countOnly = false,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		// Validate the filter before doing any work: an unknown kind name is the caller's mistake and must
		// be named, not turned into an empty result that looks like "no such symbol".
		HashSet<string>? kindFilter = SymbolKinds.Parse(kinds);

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// A dotted name is container-qualified rather than a substring of a bare name: the declaration
		// search is keyed on the leaf, and each candidate is then held to the whole-segment suffix of its
		// containing type/namespace chain. A bare name keeps the substring behaviour it has always had.
		SymbolSignatureQuery? containerQuery = null;
		if (SymbolSignature.TryParse(symbolNameFilter, out SymbolSignatureQuery parsed) && parsed.Qualified)
			containerQuery = parsed;

		Func<string, bool> predicate = containerQuery is null
			? name => name.Contains(symbolNameFilter, StringComparison.OrdinalIgnoreCase)
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

		// The result set is every match of the requested kinds; the response budget and maxSize bound what
		// one page carries, so a long result pages with startItem instead of losing matches to a cap.
		// count= names the filtered set's size.
		IReadOnlyList<(ISymbol Symbol, Solution Solution)> page = matched.Items
			.Where(item => SymbolKinds.Matches(item.Symbol, kindFilter))
			.ToList();

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
			budget: budget,
			countOnly: countOnly);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), model.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
