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

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

[McpServerToolType]
public sealed class SearchSymbolsTool
{
	public const string SearchSymbolsName = "search_symbols";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ProjectionService ProjectionService;

	public SearchSymbolsTool(InstanceRegistry instanceRegistry, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = SearchSymbolsName,
		Title = "Search symbols by name",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Searches source-declared symbols whose name contains the query (case-insensitive), across the
		solution. {OutlineDescriptions.TextNotJson} Matches are grouped file -> namespace -> type -> member, a
		matched member nesting under its (parent-only) type:

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>,<loc>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>
		where kind is one of {OutlineDescriptions.KindList}, {OutlineDescriptions.Loc}, and a type's location
		is present only when the type itself matched; {OutlineDescriptions.ListFieldQuoting}. {OutlineDescriptions.Truncation} {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over grepping
		to locate where something is declared; it searches the compiler's declared symbols.
		""")]
	public async Task<string> SearchSymbols(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Substring to match against symbol names (case-insensitive).")] string query,
		[Description("Maximum results to return. Default 50.")] int maxResults = 50)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Search every projection so declarations that compile only in a branch inactive in the loaded
		// configuration are included; dedupe by fully-qualified name across projections.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var matched = new List<(ISymbol Symbol, Solution Solution)>();
		foreach (Projection projection in projections)
		{
			foreach (ISymbol symbol in await SymbolFinder.FindSourceDeclarationsAsync(projection.Solution, name => name.Contains(query, StringComparison.OrdinalIgnoreCase)))
			{
				if (seen.Add(SymbolResolver.FullyQualifiedName(symbol)))
					matched.Add((symbol, projection.Solution));
			}
		}

		List<(ISymbol Symbol, Solution Solution)> results = matched.Take(Math.Max(0, maxResults)).ToList();

		var root = new SymbolNode();
		foreach ((ISymbol symbol, Solution symbolSolution) in results)
			SymbolPlacement.Place(root, symbol, symbolSolution, solutionDirectory);

		var builder = new OutlineBuilder();
		if (matched.Count > results.Count)
		{
			builder.Header("count", matched.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
