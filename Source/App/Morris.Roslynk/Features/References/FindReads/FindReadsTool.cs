using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Accesses;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.References.FindReads;

[McpServerToolType]
public sealed class FindReadsTool
{
	public const string FindReadsName = "find_reads";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public FindReadsTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[McpServerTool(
		Name = FindReadsName,
		Title = "Find reads of a field, property or parameter",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds the reads (including the read half of compound, increment and ref) of a field, property or parameter across the solution, resolved semantically, so
		same-named symbols in other scopes are never reported.
		{OutlineDescriptions.CommonMethodInstructions}
		The body has the find_references shape with one location per leaf and the access kind appended:
		  resolvedSymbol=<name; a parameter is written Member:parameter>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace, or "<global>">
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>,<accessKind>
		where kind is one of {OutlineDescriptions.KindList}; {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}.
		{OutlineDescriptions.AccessKinds}
		{OutlineDescriptions.Paging} {OutlineDescriptions.Project} {OutlineDescriptions.GeneratedLocations} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock}
		error=NotSupported is returned for a symbol that cannot be read or written (a method, type, event or local variable).
		{OutlineDescriptions.AccessCoverage}
		""")]
	public async Task<string> FindReads(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description(OutlineDescriptions.AccessSymbolName)] string symbolName,
		[Description("Maximum locations to return. Omit for every location (paging walks the result with startItem).")] int? maxResults = null,
		[Description("1-based first item of the page; only pass it to continue from nextStartItem=. Default 1.")] int startItem = 1,
		[Description("Page size in characters; an oversized result is returned as a page with count=/truncated=Y/nextStartItem=. Default: the response budget.")] int maxSize = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);
		return await FindReadsCoreAsync(model, instance, symbolName, maxResults, startItem, maxSize, Budget, cancellationToken);
	}

	internal Task<string> FindReadsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		int? maxResults = null,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken cancellationToken = default) =>
		AccessQuery.RunAsync(model, SymbolResolver, ProjectionService, symbolName, AccessKindText.IsRead, maxResults, startItem, maxSize, budget, cancellationToken);
}
