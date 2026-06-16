using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.SearchSymbols;

[McpServerToolType]
public sealed class SearchSymbolsTool
{
	public const string SearchSymbolsName = "search_symbols";

	private readonly InstanceRegistry InstanceRegistry;

	public SearchSymbolsTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
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
		  \t<relative/forward-slash/path.cs>
		  \t\t<namespace>
		  \t\t\t<typeKind>,<typeName>,<loc>
		  \t\t\t\t<memberKind>,<memberName>,<loc>
		where kind is one of {OutlineDescriptions.KindList}, {OutlineDescriptions.Loc}, and a type's location
		is present only when the type itself matched; {OutlineDescriptions.ListFieldQuoting}. {OutlineDescriptions.Truncation} {OutlineDescriptions.Project} {OutlineDescriptions.ErrorBlock} Prefer this over grepping
		to locate where something is declared; it searches the compiler's declared symbols.
		""")]
	public async Task<string> SearchSymbols(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Substring to match against symbol names (case-insensitive).")] string query,
		[Description("Maximum results to return. Default 50.")] int maxResults = 50)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IEnumerable<ISymbol> found = await SymbolFinder.FindSourceDeclarationsAsync(
			model.Solution,
			name => name.Contains(query, StringComparison.OrdinalIgnoreCase));

		List<ISymbol> all = found
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		List<ISymbol> results = all.Take(Math.Max(0, maxResults)).ToList();

		var root = new SymbolNode();
		foreach (ISymbol symbol in results)
			SymbolPlacement.Place(root, symbol, model.Solution, solutionDirectory);

		var builder = new OutlineBuilder();
		if (all.Count > results.Count)
		{
			builder.Header("count", all.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
