using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Callers;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Callers.GetCallees;

[McpServerToolType]
public sealed class GetCalleesTool
{
	public const string GetCalleesName = "get_callees";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public GetCalleesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Lists methods, constructors, property/event accessors and operators called by a member.
		Property reads/writes name their accessors (compound/increment reports both); e += h names
		the add accessor. A property includes its accessors; a field includes its initializer;
		constructors include initializers they run. Includes user-defined operators/conversions,
		using's Dispose, delegate method groups, nested lambdas and local functions. Abstract/interface
		declarations and auto-properties without code call nothing.

		Implicit foreach (GetEnumerator, MoveNext, Current) and await (GetAwaiter, IsCompleted, GetResult)
		calls are excluded; get_callers includes them. Explicit calls remain included.

		Solution callees nest project > folder > file > namespace > type; metadata callees appear
		under <external:AssemblyName> with empty loc. Leaves: <memberKind>,<memberName>,<loc>, plus
		parameterTypes only for overloads declared in the callee's type (parameterless overload: '()'). Constructed generics collapse to their definitions; extension
		methods include their 'this' parameter.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = GetCalleesName,
		Title = "Get the members a member calls",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetCallees(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.SymbolName)] string symbolName,
		[Description("Exclude metadata callees (BCL/NuGet).")] bool excludeExternal = false,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, _) =>
				GetCalleesCoreAsync(
					Model: model,
					Instance: instance,
					SymbolName: symbolName,
					ExcludeExternal: excludeExternal,
					StartItem: startItem,
					MaxSize: maxSize,
					CountOnly: countOnly,
					Budget: Budget
				),
			cancellationToken);
	}

	internal async Task<string> GetCalleesCoreAsync(
		SolutionModel Model,
		RoslynInstance Instance,
		string SymbolName,
		bool ExcludeExternal = false,
		int StartItem = 1,
		int MaxSize = 0,
		bool CountOnly = false,
		ResponseBudget? Budget = null,
		CancellationToken Token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, Model.Status);

		if (Model.Solution is null)
			return Failure(Error.Indexing(Instance.LoadedProjects));

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(Model.Solution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(Model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, SymbolName);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(Model.Solution, SymbolName);
			return Failure(Error.NotFound($"No symbol matched '{SymbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(SymbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union callees across every projection, deduped by stable symbol identity, so a callee that only
		// compiles in a branch inactive in the loaded configuration is still reported; the copy kept is the
		// best bound, so a parameter list rendered for an overloaded callee comes from where it binds.
		var callees = new DeclarationCopies<Solution>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ISymbol callee in await CalleeWalker.CalleesOfAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, Token))
			{
				if (ExcludeExternal && CalleePlacement.IsExternal(callee))
					continue;

				callees.Add(callee, projectionSymbol.Projection.Solution);
			}
		}

		var root = new SymbolNode();
		foreach ((ISymbol callee, Solution calleeSolution) in callees.Items)
			CalleePlacement.Place(root, callee, calleeSolution, solutionDirectory);

		PagedRender render = ItemPaging.Render(
			Model.Status,
			builder => builder.Header("resolvedSymbol", SymbolResolver.SignatureName(resolved[0].Symbol)),
			builder => root.Render(builder),
			root.Items(),
			StartItem,
			MaxSize,
			Budget,
			countOnly: CountOnly);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
