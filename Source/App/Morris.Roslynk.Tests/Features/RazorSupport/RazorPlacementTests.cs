using Microsoft.CodeAnalysis;
using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Features.RazorSupport;

/// <summary>
/// A member declared in a .razor file has a location in the virtual generated document, but its line and
/// column come from the #line mapping back into the .razor source. search_symbols, get_callees, get_callers
/// and find_implementations place symbols through SymbolPlacement/CalleePlacement, so they must name the
/// .razor file that holds the reported position. Only a location with no Razor origin (BuildRenderTree's
/// generated body) keeps the generated .g.cs path, because it has no .razor position to report.
/// </summary>
public class RazorPlacementTests
{
	private const string CounterFile = "RazorLib\n\tRazorLib\n\t\tCounter.razor\n";

	[Fact]
	public async Task WhenSearchingForARazorDeclaredMember_ThenTheRazorFileAndPositionAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Razor);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.Razor, "IncrementCount");

		Assert.DoesNotContain("error=", result);
		Assert.Contains(CounterFile, result);
		Assert.Contains("method,IncrementCount,18:15\n", result);
		Assert.DoesNotContain("_razor.g.cs", result);
	}

	[Fact]
	public async Task WhenListingTheCalleesOfARazorDeclaredMember_ThenTheCalleeIsUnderTheRazorFile()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Razor);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.Razor, "RazorLib.Counter.OnInitialized");

		Assert.DoesNotContain("error=", result);
		Assert.Contains(CounterFile, result);
		// StartAt is declared on line 11; its getter accessor maps to the get token on that line.
		Assert.Contains("method,get_StartAt,11:23\n", result);
		Assert.DoesNotContain("_razor.g.cs", result);
	}

	[Fact]
	public async Task WhenGettingTheCallersOfARazorDeclaredMember_ThenACallerWithNoRazorPositionKeepsTheGeneratedPath()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Razor);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		// IncrementCount is called only from the markup, so the caller is BuildRenderTree, whose declaration
		// spans several #line regions and therefore has no single .razor position: it keeps its generated
		// path and generated line rather than naming Counter.razor with a line from another file.
		string result = await subject.GetCallers(TestSolutions.Razor, "RazorLib.Counter.IncrementCount");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("RazorLib\n\tRazorLib/obj/RoslynkRazorGenerated\n\t\tCounter_razor.g.cs\n", result);
		Assert.Contains("method,BuildRenderTree,30:33\n", result);
		Assert.DoesNotContain("Counter.razor", result);
	}

	[Fact]
	public async Task WhenGettingTheCallersOfARazorDeclaredField_ThenMappedCallersReportTheRazorFile()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Razor);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		// CurrentCount is read by the markup (BuildRenderTree, generated only) and written by the two
		// @code members, which are declared in the .razor file and must be reported there.
		string result = await subject.GetCallers(TestSolutions.Razor, "RazorLib.Counter.CurrentCount");

		Assert.DoesNotContain("error=", result);
		Assert.Contains(CounterFile, result);
		Assert.Contains("method,IncrementCount,18:15\n", result);
		Assert.Contains("method,OnInitialized,13:26\n", result);
		Assert.Contains("Counter_razor.g.cs\n", result);
		Assert.Contains("method,BuildRenderTree,30:33\n", result);
	}

	[Fact]
	public async Task WhenPlacingARazorDeclaredSymbolDirectly_ThenThePlacementNamesTheRazorFile()
	{
		// find_implementations places through the same SymbolPlacement seam as search_symbols and
		// get_callers; the fixture has no .razor-declared implementor, so the shared seam is exercised
		// directly against a resolved Razor-declared symbol.
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Razor);
		Solution solution = instance.CurrentSolution;

		INamedTypeSymbol counter = (await solution.Projects.Single(project => project.Name == "RazorLib").GetCompilationAsync())!
			.GetTypeByMetadataName("RazorLib.Counter")!;
		ISymbol incrementCount = counter.GetMembers("IncrementCount").OfType<IMethodSymbol>().Single();
		Location location = incrementCount.Locations.First(candidate => candidate.IsInSource);

		// The premise of the bug: the tree is the virtual generated document, while the position comes from
		// the #line mapping into Counter.razor.
		Assert.EndsWith("_razor.g.cs", location.SourceTree!.FilePath);
		Assert.EndsWith("Counter.razor", location.GetDisplaySpan().Path);

		var root = new SymbolNode();
		SymbolPlacement.Place(root, incrementCount, solution, SolutionRelativePath.DirectoryOf(solution));
		var builder = new OutlineBuilder();
		root.Render(builder);

		string result = builder.ToString();
		Assert.Contains(CounterFile, result);
		Assert.Contains("method,IncrementCount,18:15\n", result);
		Assert.DoesNotContain("_razor.g.cs", result);
	}
}
