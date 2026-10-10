using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

public class MultiQueryGeneratedSearchTests
{
	[Fact]
	public async Task WhenSearchSymbolsRunsInABatch_ThenGeneratedDeclarationsAreFound()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.MultiQuery(TestSolutions.Generator,
		[
			new(MultiQueryOp.search_symbols, MultiQueryTestHelpers.Args(("query", MultiQueryTestHelpers.Json("OnlyGenerated")))),
		]);

		Assert.Contains("operations=1", envelope);
		Assert.Contains("slot=1 tool=search_symbols", envelope);
		Assert.Contains("class,OnlyGenerated,23:22", envelope);
	}
}
