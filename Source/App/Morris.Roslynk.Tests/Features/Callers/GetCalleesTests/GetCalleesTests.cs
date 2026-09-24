using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCalleesTests;

public class GetCalleesTests
{
	[Fact]
	public async Task WhenAMethodCallsOthers_ThenItsCalleesAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

		Assert.Contains("resolvedSymbol=SimpleLibrary.Caller.Run", result);
		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Greeter\n", result);
		Assert.Contains("method,Greet,", result);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenAnOverloadIsTargetedBySignature_ThenOnlyThatOverloadsCalleesAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Ledger.Add(int, int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Ledger.Add(int, int)", result);
		// Add(int, int) invokes Add(int); the reverse is not true.
		Assert.Contains("method,Add,", result);
	}

	[Fact]
	public async Task WhenTheNameMatchesSeveralOverloads_ThenAmbiguousCandidatesAreDistinguishable()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Ledger.Add");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add(int)\n", result);
		Assert.Contains("candidate=SimpleLibrary.Ledger.Add(int, int)\n", result);
	}

	[Fact]
	public async Task WhenThereAreMoreCalleesThanMaxResults_ThenABoundedPageIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		// RunAll invokes eight distinct Pick overloads plus two default constructors; page them down to three.
		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.OverloadCaller.RunAll", maxResults: 3);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("count=10", result);
		Assert.Contains("truncated=Y", result);
	}
}
