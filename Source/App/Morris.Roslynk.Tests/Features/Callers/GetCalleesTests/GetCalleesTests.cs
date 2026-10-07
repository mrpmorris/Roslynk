using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCalleesTests;

public class GetCalleesTests
{
	[Fact]
	public async Task WhenAMethodCallsOtherMembers_ThenTheCalleesAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

		Assert.Contains("resolvedSymbol=SimpleLibrary.Caller.Run", result);
		Assert.DoesNotContain("error=", result);
		// 'new Greeter().Greet(name)': the constructor call and the invocation both appear.
		Assert.Contains("class,Greeter\n", result);
		Assert.Contains("method,Greet,", result);
	}

	[Fact]
	public async Task WhenAMethodCallsAnOverload_ThenOnlyThatOverloadIsReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Ledger.Add(int, int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Ledger.Add(int, int)", result);
		// Add(int, int) loops Add(amount) and reads Total at return; the reverse is not true. Add is
		// overloaded, so the leaf names the overload by its parameter types.
		Assert.Matches(@"\tmethod,Add,\d+:\d+,int\n", result);
		Assert.DoesNotMatch(@"method,Add,\d+:\d+,int\|int", result);
		Assert.Contains("method,get_Total,", result);
	}

	[Fact]
	public async Task WhenAPropertyIsRead_ThenItsGetterIsTheCallee()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Ledger.Add(int)");

		Assert.DoesNotContain("error=", result);
		// 'Total += amount' is a compound assignment on Total: the setter runs and the getter reads,
		// so both accessors appear.
		Assert.Contains("method,set_Total,", result);
		Assert.Contains("method,get_Total,", result);
	}

	[Fact]
	public async Task WhenTheMemberHasNoBody_ThenNoCalleesAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.IGreeter.Greet");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.IGreeter.Greet", result);
		// The interface declaration sits under the file that declares it, without a member leaf below.
		string body = result[(result.IndexOf('\n') + 1)..];
		Assert.DoesNotContain("method,Greet,", body);
	}

	[Fact]
	public async Task WhenTheMemberIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Contains("error=NotFound", result);
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
	public async Task WhenCallsComeFromBothBranchesOfAConditional_ThenBothCalleesAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Conditional, "ConditionalLib.Caller2.DebugCall");

		Assert.DoesNotContain("error=", result);
		// The DEBUG body's callee is reported from the base projection; the same body compiled under the
		// !DEBUG variant contributes the #else callees of the sibling, unioned without duplicates.
		Assert.Contains("method,Ping,", result);
		Assert.Contains("class,Target\n", result);
	}

	[Fact]
	public async Task WhenALocalFunctionIsQueried_ThenItsOwnCallsAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.LocalFunctions, "LocalFunctionLib.ParentClass.Widget.Method1.localMethod");

		Assert.DoesNotContain("error=", result);
		// localMethod calls inner(string); inner is nested under Method1 in the outline.
		Assert.Contains("localfunction,inner,", result);
	}

	[Fact]
	public async Task WhenAMethodCallsAMemberThroughALambda_ThenTheLambdaTargetsCallIsReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.OverloadCaller.RunAll");

		Assert.DoesNotContain("error=", result);
		// Every Overloads member is called once from RunAll, overloads resolved to distinct members; the
		// implicit 'new Overloads()' reports the constructor as method,..ctor like get_members does.
		Assert.Contains("method,Pick,", result);
		Assert.Contains("method,.ctor,", result);
	}
}
