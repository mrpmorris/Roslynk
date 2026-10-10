using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>
/// The issue #81 repro, batched: a generated member no hand-written code mentions resolves in a
/// multi_query call, in every slot that names it.
/// </summary>
public class GeneratedDeclarationBatchTests
{
	[Fact]
	public async Task WhenBatched_ThenAnUnmentionedGeneratedMemberResolves()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Generator);
		SolutionModel model = await instance.ReadModelAsync();

		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.ExecuteBatchAsync(
			model,
			instance,
			MultiQueryCatalog.Entries,
			[
				new MultiQueryOperation(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("ConsumerLib.Ctx.ReadValue2")))),
				new MultiQueryOperation(MultiQueryOp.find_references, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("ConsumerLib.Ctx.ReadValue2")))),
			]);

		Assert.DoesNotContain("error=", envelope);
		Assert.Contains("public int ReadValue2() => 2;", envelope);
	}
}
