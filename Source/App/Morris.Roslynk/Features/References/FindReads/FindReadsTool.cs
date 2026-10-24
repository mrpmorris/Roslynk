using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Accesses;
using Morris.Roslynk.Infrastructure.Documentation;
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

	[Description("""
		SupportsPaging=Y

		Finds reads, including compound/increment/ref, using the shared access contract.
		Body follows find_references with one location per leaf.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = FindReadsName,
		Title = "Find reads of a field, property or parameter",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindReads(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(OutlineDescriptions.AccessSymbolName)] string symbolName,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		return await QueryExecution.QueryAsync(
			instance,
			(model, token) =>
				FindReadsCoreAsync(
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

	internal Task<string> FindReadsCoreAsync(
		SolutionModel Model,
		RoslynInstance Instance,
		string SymbolName,
		int StartItem = 1,
		int MaxSize = 0,
		bool CountOnly = false,
		ResponseBudget? Budget = null,
		CancellationToken CancellationToken = default) =>
		AccessQuery.RunAsync(Model, Instance, SymbolResolver, ProjectionService, SymbolName, AccessKindText.IsRead, StartItem, MaxSize, CountOnly, Budget, CancellationToken);
}
