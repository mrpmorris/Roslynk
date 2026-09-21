using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

public class MultiQueryTests
{
	private static async Task<(MultiQueryTool Subject, InstanceRegistry Registry)> CreateAsync()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		return (new MultiQueryTool(provider, registry), registry);
	}

	[Fact]
	public async Task WhenMultiQueryReturnsOneResultPerOperationInRequestOrder_ThenSlotsCarryEachToolsOwnOutput()
	{
		(MultiQueryTool subject, _) = await CreateAsync();
		var operations = new List<MultiQueryOperation>
		{
			new(MultiQueryOp.get_symbol, Args(("symbolName", Json("SimpleLibrary.Greeter")))),
			new(MultiQueryOp.get_members, Args(("typeName", Json("SimpleLibrary.Greeter")))),
		};

		string envelope = await subject.MultiQuery(TestSolutions.Simple, operations);

		Assert.Contains("operations=2", envelope);
		Assert.Contains("boundary=", envelope);
		Assert.Contains("slot=1 tool=get_symbol", envelope);
		Assert.Contains("slot=2 tool=get_members", envelope);
		Assert.Contains("public class Greeter : IGreeter", envelope);   // get_symbol's verbatim declaration
		Assert.Contains("resolvedType=SimpleLibrary.Greeter", envelope); // get_members' own header
	}

	[Fact]
	public async Task WhenAnOperationFailsTheBatchStillReturnsTheOtherResults_ThenTheErrorSitsInItsOwnSlot()
	{
		(MultiQueryTool subject, _) = await CreateAsync();
		var operations = new List<MultiQueryOperation>
		{
			new(MultiQueryOp.get_symbol, Args(("symbolName", Json("SimpleLibrary.Greeter")))),
			new(MultiQueryOp.get_symbol, Args(("symbolName", Json("SimpleLibrary.DoesNotExist")))),
		};

		string envelope = await subject.MultiQuery(TestSolutions.Simple, operations);

		Assert.Contains("operations=2", envelope);
		Assert.Contains("error=NotFound", envelope);
		Assert.Contains("public class Greeter : IGreeter", envelope);
	}

	[Fact]
	public async Task WhenOperationsListIsEmptyThenTheBatchIsRejected_WithInvalid()
	{
		(MultiQueryTool subject, _) = await CreateAsync();

		string result = await subject.MultiQuery(TestSolutions.Simple, []);

		Assert.StartsWith("error=Invalid", result);
	}

	[Fact]
	public async Task WhenAnOperationNamesAnUnknownTool_ThenItsSlotGetsNotFoundWithCandidates()
	{
		(MultiQueryTool subject, InstanceRegistry registry) = await CreateAsync();
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Simple);
		SolutionModel model = await instance.ReadModelAsync();

		// Reached only through the internal seam: over the wire the enum binding refuses other names.
		var emptyCatalog = new Dictionary<string, MultiQueryCatalog.OpEntry>(StringComparer.Ordinal);
		string envelope = await subject.ExecuteBatchAsync(
			model,
			instance,
			emptyCatalog,
			[new MultiQueryOperation((MultiQueryOp)99, Args(("foo", Json("bar"))))]);

		Assert.Contains("error=NotFound", envelope);
		Assert.Contains("candidate=get_symbol", envelope);
	}

	[Fact]
	public async Task WhenAnOperationHasAnUnknownArgument_ThenItsSlotIsInvalid()
	{
		(MultiQueryTool subject, _) = await CreateAsync();

		string envelope = await subject.MultiQuery(
			TestSolutions.Simple,
			[new MultiQueryOperation(MultiQueryOp.get_symbol, Args(("foo", Json("bar"))))]);

		Assert.Contains("error=Invalid", envelope);
		Assert.Contains("'foo' is not a parameter of 'get_symbol'", envelope);
	}

	[Fact]
	public async Task WhenARequiredArgumentIsMissing_ThenItsSlotIsInvalid()
	{
		(MultiQueryTool subject, _) = await CreateAsync();

		string envelope = await subject.MultiQuery(
			TestSolutions.Simple,
			[new MultiQueryOperation(MultiQueryOp.get_symbol)]);

		Assert.Contains("error=Invalid", envelope);
		Assert.Contains("Missing required parameter 'symbolName'", envelope);
	}

	[Fact]
	public async Task WhenASolutionIdIsPassedInsideAnOperation_ThenItIsRejectedAsAnUnknownArgument()
	{
		(MultiQueryTool subject, _) = await CreateAsync();

		string envelope = await subject.MultiQuery(
			TestSolutions.Simple,
			[new MultiQueryOperation(
				MultiQueryOp.get_symbol,
				Args(("solutionId", Json(TestSolutions.Simple)), ("symbolName", Json("SimpleLibrary.Greeter"))))]);

		Assert.Contains("error=Invalid", envelope);
		Assert.Contains("'solutionId' is not a parameter of 'get_symbol'", envelope);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenTheWholeBatchIsIndexing()
	{
		using var registry = new InstanceRegistry();
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		// A nonexistent solution path faults its load, so the model has no snapshot - deterministic, unlike
		// racing a warm real load. The convention is the tools': null Solution -> Indexing.
		string missing = TestSolutions.Simple.Replace(".slnx", ".missing.slnx");

		string result = await subject.MultiQuery(
			missing,
			[new MultiQueryOperation(MultiQueryOp.get_symbol, Args(("symbolName", Json("SimpleLibrary.Greeter"))))]);

		Assert.StartsWith("error=Indexing", result);
	}

	private static IReadOnlyDictionary<string, JsonElement> Args(params (string Key, JsonElement Value)[] pairs) =>
		MultiQueryTestHelpers.Args(pairs);

	private static JsonElement Json(string value) => MultiQueryTestHelpers.Json(value);
}
