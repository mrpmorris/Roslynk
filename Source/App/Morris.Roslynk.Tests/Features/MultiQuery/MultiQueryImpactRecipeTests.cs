using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>
/// Issue #22 acceptance: the documented impact-analysis recipe (get_symbol + find_references +
/// get_callers + find_implementations + get_type_hierarchy in one multi_query call) runs cleanly
/// against a sample solution - every slot carries that tool's own output and none is error=Invalid.
/// The recipe deliberately uses each tool's own argument name (get_callers is methodName,
/// get_type_hierarchy is typeName): multi_query rejects unknown keys.
/// </summary>
public class MultiQueryImpactRecipeTests
{
	[Fact]
	public async Task WhenTheImpactRecipeRunsAgainstASampleSolution_ThenEverySlotIsPopulatedAndNoneIsInvalid()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.MultiQuery(
			TestSolutions.Simple,
			[
				new(MultiQueryOp.get_symbol, Args(("symbolName", Json("SimpleLibrary.Greeter")))),
				new(MultiQueryOp.find_references, Args(("symbolName", Json("SimpleLibrary.Greeter.Greet")))),
				new(MultiQueryOp.get_callers, Args(("methodName", Json("SimpleLibrary.Greeter.Greet")))),
				new(MultiQueryOp.find_implementations, Args(("symbolName", Json("SimpleLibrary.IGreeter.Greet")))),
				new(MultiQueryOp.get_type_hierarchy, Args(("typeName", Json("SimpleLibrary.Greeter")))),
			]);

		Assert.Contains("operations=5", envelope);
		Assert.DoesNotContain("error=Invalid", envelope);
		Assert.DoesNotContain("error=NotFound", envelope);

		Assert.Contains("slot=1 tool=get_symbol", envelope);
		Assert.Contains("public class Greeter : IGreeter", envelope);      // get_symbol's verbatim declaration

		Assert.Contains("slot=2 tool=find_references", envelope);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Greeter.Greet", envelope);
		Assert.Contains("method,Run,", envelope);                          // Caller.Run is the reference site

		Assert.Contains("slot=3 tool=get_callers", envelope);
		Assert.Contains("method,Run,", envelope);                          // Caller.Run calls Greeter.Greet

		Assert.Contains("slot=4 tool=find_implementations", envelope);
		Assert.Contains("resolvedSymbol=SimpleLibrary.IGreeter.Greet", envelope);
		Assert.Contains("Greeter", envelope);                              // Greeter.Greet implements it

		Assert.Contains("slot=5 tool=get_type_hierarchy", envelope);
		Assert.Contains("resolvedType=SimpleLibrary.Greeter", envelope);
		Assert.Contains("interfaces", envelope);
		Assert.Contains("IGreeter", envelope);
	}

	private static IReadOnlyDictionary<string, JsonElement> Args(params (string Key, JsonElement Value)[] pairs) =>
		MultiQueryTestHelpers.Args(pairs);

	private static JsonElement Json(string value) => MultiQueryTestHelpers.Json(value);
}
