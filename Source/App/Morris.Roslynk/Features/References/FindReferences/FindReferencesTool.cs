using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
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

	[McpServerTool(
		Name = FindReferencesName,
		Title = "Find references to a symbol",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindReferences(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified symbol name; reuse names returned by discovery. Include parameter types to select one overload.")] string symbolName,
		[Description("Maximum reference locations to return; omit for every reference.")] int? maxResults = null,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);
		return await FindReferencesCoreAsync(model, instance, symbolName, maxResults, startItem, maxSize, Budget, cancellationToken);
	}

	internal async Task<string> FindReferencesCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		int? maxResults = null,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken cancellationToken = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		// Run against every projection (per-TFM projects + #if-toggle variants) so references in conditionally
		// compiled branches that are inactive in the loaded configuration are still found.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(solution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName, cancellationToken);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(symbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		ISymbol representative = resolved[0].Symbol;

		// Union references from each projection instance of the symbol, deduped by file + span so a reference
		// in shared (non-conditional) code is reported once even though it is seen in several projections.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hits = new List<(Location Location, Solution Solution)>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, cancellationToken))
			{
				foreach (ReferenceLocation reference in referenced.Locations)
				{
					if (!reference.Location.IsInSource)
						continue;

					FileLinePositionSpan referenceSpan = reference.Location.GetDisplaySpan();
					string key = $"{referenceSpan.Path}|{referenceSpan.StartLinePosition}-{referenceSpan.EndLinePosition}";
					if (seen.Add(key))
						hits.Add((reference.Location, projectionSymbol.Projection.Solution));
				}
			}
		}

		// The result set: capped only when the caller asked for a cap, so omitting maxResults pages through
		// everything. count= names the size of this set.
		IReadOnlyList<(Location Location, Solution Solution)> page = maxResults is int cap
			? hits.Take(Math.Max(0, cap)).ToList()
			: hits;
		bool capped = hits.Count > page.Count;

		var root = new SymbolNode();
		foreach ((Location location, Solution locationSolution) in page)
		{
			EnclosingPath enclosing = await EnclosingDeclaration.ResolveAsync(locationSolution, location, cancellationToken);
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
			model.Status,
			builder => builder.Header("resolvedSymbol", SymbolResolver.SignatureName(representative)),
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
			budget: Budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
