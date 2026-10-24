using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Accesses;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.References.FindWrites;

[McpServerToolType]
public sealed class FindWritesTool
{
	public const string FindWritesName = "find_writes";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public FindWritesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds writes (assign/compound/increment/ref/out/init) using the shared access contract.
		Body follows find_references with one location per leaf.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = FindWritesName,
		Title = "Find writes to a field, property or parameter",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindWrites(
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
			(model, token) => FindWritesCoreAsync(model, instance, symbolName, startItem, maxSize, countOnly, Budget, token),
			cancellationToken);
	}

	internal Task<string> FindWritesCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		int startItem = 1,
		int maxSize = 0,
		bool countOnly = false,
		ResponseBudget? budget = null,
		CancellationToken cancellationToken = default) =>
		AccessQuery.RunAsync(model, instance, SymbolResolver, ProjectionService, symbolName, AccessKindText.IsWrite, startItem, maxSize, countOnly, budget, cancellationToken);
}
