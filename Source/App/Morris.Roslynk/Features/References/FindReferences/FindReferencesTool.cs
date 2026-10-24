using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.References;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.References.FindReferences;

[McpServerToolType]
public sealed class FindReferencesTool
{
	public const string FindReferencesName = "find_references";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public FindReferencesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds semantic references, including partial classes/code-behind.
		Body: project > folder > file > namespace > type > member. Shared declarations appear once;
		type locations appear only when the declaration references the target; collapsed references
		use pipe-delimited loc lists.

		Combine with get_callers/get_callees/find_implementations/get_type_hierarchy via multi_query for
		usage/impact analysis.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = FindReferencesName,
		Title = "Find references to a symbol",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindReferences(
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
			(model, token) =>
				FindReferencesCoreAsync(
					Model: model,
					Instance: instance,
					SymbolName: symbolName,
					StartItem: startItem,
					MaxSize: maxSize,
					CountOnly: countOnly,
					Budget: Budget,
					CancellationToken: token
				),
			cancellationToken);
	}

	internal async Task<string> FindReferencesCoreAsync(
		SolutionModel Model,
		RoslynInstance Instance,
		string SymbolName,
		int StartItem = 1,
		int MaxSize = 0,
		bool CountOnly = false,
		ResponseBudget? Budget = null,
		CancellationToken CancellationToken = default)
	{
		string Failure(Error error) => OutlineError.Format(error, Model.Status);

		if (Model.Solution is null)
			return Failure(Error.Indexing(Instance.LoadedProjects));

		Solution solution = Model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		// Run against every projection (per-TFM projects + #if-toggle variants) so references in conditionally
		// compiled branches that are inactive in the loaded configuration are still found.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(solution, CancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, SymbolName, CancellationToken);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(solution, SymbolName);
			return Failure(Error.NotFound($"No symbol matched '{SymbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(SymbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		ISymbol representative = resolved[0].Symbol;

		// Union references from each projection Instance of the symbol, deduped by file + span so a reference
		// in shared (non-conditional) code is reported once even though it is seen in several projections.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hits = new List<(Location Location, Solution Solution)>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, CancellationToken))
			{
				bool dispose = SpuriousDisposeReferences.IsDisposeMethod(projectionSymbol.Symbol);

				foreach (ReferenceLocation reference in referenced.Locations)
				{
					if (!reference.Location.IsInSource)
						continue;

					// Roslyn 5.9 reports a plain disposable local declaration at its type Token as a Dispose
					// reference; such a location is not a reference, while a real using or call is.
					if (dispose && await SpuriousDisposeReferences.IsSpuriousAsync(reference.Location, CancellationToken))
						continue;

					FileLinePositionSpan referenceSpan = reference.Location.GetDisplaySpan();
					string key = $"{referenceSpan.Path}|{referenceSpan.StartLinePosition}-{referenceSpan.EndLinePosition}";
					if (seen.Add(key))
						hits.Add((reference.Location, projectionSymbol.Projection.Solution));
				}
			}
		}

		// The result set is every hit; the response budget and MaxSize bound what one page carries, so a
		// long result pages with StartItem instead of losing hits to a cap. count= names the set's size.
		IReadOnlyList<(Location Location, Solution Solution)> page = hits;

		var root = new SymbolNode();
		foreach ((Location location, Solution locationSolution) in page)
		{
			EnclosingPath enclosing = await EnclosingDeclaration.ResolveAsync(locationSolution, location, CancellationToken);
			FileLinePositionSpan span = location.GetDisplaySpan();

			SymbolNode fileParent = location.SourceTree is SyntaxTree tree && ProjectName.Of(locationSolution, tree) is string project
				? root.Child(project)
				: root;
			SymbolNode node = fileParent
				.ChildPath(GeneratedSource.MarkPath(SolutionRelativePath.Of(solutionDirectory, span.Path)!, locationSolution, location.SourceTree))
				.Child(enclosing.Namespace);
			foreach (EnclosingSegment segment in enclosing.Segments)
				node = node.Child($"{segment.Kind},{OutlineBuilder.Field(segment.Name)}");

			node.AddLocation(
				span.StartLinePosition.Line + 1,
				span.StartLinePosition.Character + 1,
				span.EndLinePosition.Line + 1,
				span.EndLinePosition.Character + 1);
		}

		PagedRender render = ItemPaging.Render(
			Model.Status,
			builder => builder.Header("resolvedSymbol", SymbolResolver.SignatureName(representative)),
			capHeaders: null,
			renderBody: builder => root.Render(builder),
			items: root.Items(),
			startItem: StartItem,
			maxSize: MaxSize,
			budget: Budget,
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
