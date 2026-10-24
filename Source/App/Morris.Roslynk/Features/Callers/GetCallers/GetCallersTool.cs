using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Callers;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.References;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Callers.GetCallers;

[McpServerToolType]
public sealed class GetCallersTool
{
	public const string GetCallersName = "get_callers";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public GetCallersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds callers, including implicit calls: foreach/await enumeration and awaiter members
		(GetEnumerator/MoveNext/Current/Dispose, GetAwaiter/IsCompleted/GetResult, async equivalents),
		using/await using Dispose/DisposeAsync, deconstruction/positional patterns, collection Add,
		operators/conversions (+=/++/implicit/conditional true/false), query methods, interpolated-string
		handlers, implicit base(), and Length/Count for ^/.. . See get_callees for its narrower coverage.

		Body: project > folder > file > namespace > containing type; leaves name callers and declaration locations.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = GetCallersName,
		Title = "Get callers of a method",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetCallers(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.SymbolName)] string symbolName,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, _) =>
				GetCallersCoreAsync(
					Model: model,
					Instance: instance,
					SymbolName: symbolName,
					StartItem: startItem,
					MaxSize: maxSize,
					CountOnly: countOnly,
					Budget: Budget
				),
			cancellationToken);
	}

	internal async Task<string> GetCallersCoreAsync(
		SolutionModel Model,
		RoslynInstance Instance,
		string SymbolName,
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
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, SymbolName, Token);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(Model.Solution, SymbolName);
			return Failure(Error.NotFound($"No symbol matched '{SymbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(SymbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union callers across every projection, deduped by stable symbol identity, so a caller that only
		// compiles in a branch inactive in the loaded configuration is still reported. FindCallersAsync finds
		// explicit calls and the compiler-inserted calls Roslyn indexes (foreach GetEnumerator/MoveNext/Current,
		// await GetAwaiter, using Dispose, deconstruction, collection-initializer Add, operator tokens incl.
		// += and ++, implicit base()); ImplicitCallerScan adds the rest, gated on the target's shape so
		// ordinary targets pay nothing. The copy kept is the best bound, so the placement always describes a
		// signature that bound.
		var callers = new DeclarationCopies<Solution>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			Solution solution = projectionSymbol.Projection.Solution;

			foreach (SymbolCallerInfo caller in await SymbolFinder.FindCallersAsync(projectionSymbol.Symbol, solution, Token))
			{
				if (SpuriousDisposeReferences.IsDisposeMethod(caller.CalledSymbol)
					&& await SpuriousDisposeReferences.AreAllSpuriousAsync(caller.Locations, Token))
				{
					continue;
				}

				callers.Add(CallerSymbol.Normalize(caller.CallingSymbol), solution);
			}

			foreach (ISymbol caller in await ImplicitCallerScan.CallersAsync(
				projectionSymbol.Symbol,
				projectionSymbol.Projection,
				isBaseProjection: ReferenceEquals(projectionSymbol.Projection, resolved[0].Projection),
				Token))
			{
				callers.Add(caller, solution);
			}
		}

		var root = new SymbolNode();
		foreach ((ISymbol caller, Solution callerSolution) in callers.Items)
			SymbolPlacement.Place(root, caller, callerSolution, solutionDirectory);

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
