using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
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

	[McpServerTool(
		Name = FindImplementationsName,
		Title = "Find implementations",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds the implementations or overrides of an interface, interface member, or abstract member, resolved
		by fully-qualified name.
		{OutlineDescriptions.ProjectionCoverage}
		{OutlineDescriptions.CommonMethodInstructions}
		Implementors are grouped file -> namespace, each as '<typeKind>,<typeName>,<loc>' where {OutlineDescriptions.Loc}:
		  resolvedSymbol=<name, parameter types included for a method or indexer>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>,<loc>
		{OutlineDescriptions.Paging} {OutlineDescriptions.ListFieldQuoting} {OutlineDescriptions.Project} {OutlineDescriptions.GeneratedLocations} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading files to find implementors; it walks the
		compiler's type graph, not text.
		""")]
	public async Task<string> FindImplementations(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the interface/abstract symbol, e.g. 'MyNamespace.IMyType'. {OutlineDescriptions.SymbolNameGrammar}")] string symbolName,
		[Description("1-based first item of the page; only pass it to continue from nextStartItem=. Default 1.")] int startItem = 1,
		[Description("Page size in characters; an oversized result is returned as a page with count=/truncated=Y/nextStartItem=. Default: the response budget.")] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await FindImplementationsCoreAsync(model, instance, symbolName, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> FindImplementationsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

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
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
