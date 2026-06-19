using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Features.Symbols.FindImplementations;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetTypeHierarchy;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features;

/// <summary>
/// Each multi-projection tool must surface symbols/usages declared in a branch (<c>#else</c>) that is inactive
/// in the loaded (DEBUG) configuration. The ConditionalSolution fixture pairs a DEBUG declaration with an
/// #else one for each tool; these assert the #else member appears.
/// </summary>
public class ConditionalBranchCoverageTests
{
	[Fact]
	public async Task FindImplementations_FindsImplementorInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new FindImplementationsTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindImplementations(TestSolutions.Conditional, "ConditionalLib.IShape");

		Assert.Contains("Circle", result);
	}

	[Fact]
	public async Task GetTypeHierarchy_FindsDerivedTypeInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetTypeHierarchy(TestSolutions.Conditional, "ConditionalLib.Animal");

		Assert.Contains("ConditionalLib.Cat", result);
	}

	[Fact]
	public async Task GetMembers_FindsMemberInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.Conditional, "ConditionalLib.Box");

		Assert.Contains("ReleaseOnly", result);
	}

	[Fact]
	public async Task SearchSymbols_FindsTypeDeclaredOnlyInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Conditional, "Widget");

		Assert.Contains("ReleaseWidget", result);
	}

	[Fact]
	public async Task GetCallers_FindsCallerInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.Conditional, "ConditionalLib.Target.Ping");

		Assert.Contains("ReleaseCall", result);
	}

	[Fact]
	public async Task GetSymbol_ResolvesSymbolDeclaredOnlyInElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbol(TestSolutions.Conditional, "ConditionalLib.ReleaseWidget");

		Assert.DoesNotContain("#error=NotFound", result);
		Assert.Contains("ReleaseWidget", result);
	}

	[Fact]
	public async Task FindDefinition_ResolvesAUsageInsideTheElseBranch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new FindDefinitionTool(registry, new SymbolResolver(), new ProjectionService());

		string navigation = Path.Combine(Path.GetDirectoryName(TestSolutions.Conditional)!, "ConditionalLib", "Navigation.cs");

		// Line 10 is the #else 'return new Target();'; column 15 lands inside 'Target'.
		string result = await subject.FindDefinition(TestSolutions.Conditional, navigation, 10, 15);

		Assert.Contains("#fullName=ConditionalLib.Target", result);
	}
}
