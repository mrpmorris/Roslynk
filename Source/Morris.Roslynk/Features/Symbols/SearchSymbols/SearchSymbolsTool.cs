using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

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
		"""
		Searches source-declared symbols whose name contains the query (case-insensitive), across the
		solution. Returns up to maxResults matches with a 'truncated' flag when there are more.
		""")]
	public async Task<SearchSymbolsResult> SearchSymbols(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Substring to match against symbol names (case-insensitive).")] string query,
		[Description("Maximum results to return. Default 50.")] int maxResults = 50)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		SearchSymbolsResult Success(IReadOnlyList<SymbolSearchResult> results, bool truncated) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, Results = results, Truncated = truncated };

		SearchSymbolsResult Failure(Error error) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, Error = error };

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IEnumerable<ISymbol> found = await SymbolFinder.FindSourceDeclarationsAsync(
			model.Solution,
			name => name.Contains(query, StringComparison.OrdinalIgnoreCase));

		List<SymbolSearchResult> all = found
			.Select(symbol => new SymbolSearchResult(SymbolResolver.FullyQualifiedName(symbol), symbol.Kind.ToString()))
			.DistinctBy(result => result.FullName, StringComparer.Ordinal)
			.ToList();

		SymbolSearchResult[] results = all.Take(Math.Max(0, maxResults)).ToArray();
		return Success(results, truncated: all.Count > results.Length);
	}
}
