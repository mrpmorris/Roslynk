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

		Finds the methods, constructors, property/event accessors and operators that the named member's
		code calls. A property read or write names the accessor that runs (a compound assignment or
		increment reports both), 'e += h' names the event's add accessor, a property is its accessors and
		a field its initializer, and a constructor also reports the calls in the field/property
		initializers it runs. User-defined operators and conversions, the Dispose a using runs, and a
		method group passed as a delegate are included. Lambdas and local functions declared inside the
		member belong to it. A member with no code in the solution (an abstract or interface declaration,
		an auto-property) calls nothing.

		The compiler-inserted plumbing for foreach (GetEnumerator, MoveNext, Current) and await
		(GetAwaiter, IsCompleted, GetResult) is not included - get_callers does include it. An explicit
		.GetAwaiter() call is.

		Solution callees group project -> folder -> file -> namespace -> type; callees from referenced
		assemblies (BCL, NuGet) appear under one <external:AssemblyName> line per assembly with an empty
		loc. Each leaf is <memberKind>,<memberName>,<loc> plus a parameterTypes field only when the
		callee's own type declares more than one method of that name (inherited overloads do not count;
		the parameterless overload is '()'). Callees are reported as declared: generic instantiations
		(List<int>.Add) collapse to the generic definition, and an extension method lists its 'this'
		parameter.
		""")]
	[McpServerTool(
		Name = GetCalleesName,
		Title = "Get the members a member calls",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetCallees(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified member name; reuse names returned by discovery. Include parameter types to select one overload.")] string memberName,
		[Description("Leave out callees declared in referenced assemblies (BCL, NuGet), listing only the solution's own members.")] bool excludeExternal = false,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetCalleesCoreAsync(model, instance, memberName, excludeExternal, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> GetCalleesCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string memberName,
		bool excludeExternal = false,
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
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, memberName);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, memberName);
			return Failure(Error.NotFound($"No symbol matched '{memberName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(memberName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union callees across every projection, deduped by stable symbol identity, so a callee that only
		// compiles in a branch inactive in the loaded configuration is still reported; the copy kept is the
		// best bound, so a parameter list rendered for an overloaded callee comes from where it binds.
		var callees = new DeclarationCopies<Solution>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ISymbol callee in await CalleeWalker.CalleesOfAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, token))
			{
				if (excludeExternal && CalleePlacement.IsExternal(callee))
					continue;

				callees.Add(callee, projectionSymbol.Projection.Solution);
			}
		}

		var root = new SymbolNode();
		foreach ((ISymbol callee, Solution calleeSolution) in callees.Items)
			CalleePlacement.Place(root, callee, calleeSolution, solutionDirectory);

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
