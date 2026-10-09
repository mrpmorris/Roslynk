using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiTarget;

/// <summary>
/// The MultiTarget fixture's <c>Api.Accepts(Type)</c> parameter type binds in net8.0 but is an error type in
/// netstandard2.0, so the two copies render different signatures. They are one declaration and must merge.
/// </summary>
public class MultiTargetMergeTests
{
	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenMembersAreListedOnce()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.MultiTarget, "Repro.Api");

		Assert.Equal(1, CountOccurrences(result, "method,Accepts,"));
		Assert.Equal(1, CountOccurrences(result, "method,Count,"));
	}

	[Fact]
	public async Task WhenEveryTargetFrameworkBindsTheParameterTypes_ThenMembersAreStillListedOnce()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.MultiTarget, "Repro.Api");

		Assert.Equal(1, CountOccurrences(result, "method,Echo,"));
	}

	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenNameLookupIsNotAmbiguous()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.MultiTarget, "Repro.Api.Accepts");

		Assert.DoesNotContain("error=Ambiguous", result);
		Assert.Contains("resolvedSymbol=Repro.Api.Accepts(", result);
	}

	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenGetSymbolIsNotAmbiguous()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbol(TestSolutions.MultiTarget, "Repro.Api.Accepts");

		Assert.DoesNotContain("error=", result);
	}

	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenGetSymbolBodyIsNotAmbiguous()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbolBody(TestSolutions.MultiTarget, "Repro.Api.Accepts");

		Assert.DoesNotContain("error=", result);
	}

	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenGetCallersIsNotAmbiguous()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.MultiTarget, "Repro.Api.Accepts");

		Assert.DoesNotContain("error=Ambiguous", result);
	}

	[Fact]
	public async Task WhenOneTargetFrameworkCannotBindAParameterType_ThenSearchReportsOneLocation()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.MultiTarget, "Accepts");

		Assert.Equal(1, CountOccurrences(result, "method,Accepts,"));
		Assert.DoesNotContain("|", result);
	}

	private static int CountOccurrences(string text, string value)
	{
		int count = 0;
		for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
			count++;

		return count;
	}
}
