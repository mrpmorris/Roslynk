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

		Finds the reads of a field, property or parameter - including the read half of compound, increment
		and ref - across the solution, resolved semantically.
		The body has the find_references shape with one location per leaf and the access kind appended:
		```
		  resolvedSymbol=<name; a parameter is written Member:parameter>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace, or "<global>">
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>,<accessKind>
		```
		Access kinds:

		- read; assign: simple '='.
		- compound: '+=', '??=', etc.; increment: '++'/'--'.
		- ref: passed by ref, 'ref x', '&x'; out: passed as out.
		- init: declaration, object/with initializer, attribute named argument, or assignment to
		  a constructor's own field/property in that constructor.

		compound, increment and ref appear in both find_reads and find_writes; do not sum their counts.
		nameof, documentation crefs and named call arguments ('M(value: 1)') are excluded.
		error=NotSupported is returned for a symbol that cannot be read or written (a method, type, event
		or local variable).
		""")]
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
		[Description("Maximum locations to return; omit for every location.")] int? maxResults = null,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
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
