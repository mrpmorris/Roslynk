using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.RazorSupport;

/// <summary>
/// Razor-generated members whose declaration crosses #line regions (an @inject property, whose directive
/// maps only its type and name tokens, and BuildRenderTree, whose markup maps expression by expression)
/// still belong to the .razor source: get_symbol_body and get_members report the Razor path, the mapped
/// location and the Razor text rather than the generated .g.cs document.
/// </summary>
public class RazorSymbolMappingTests
{
	private const string PanelRelativePath = "RazorInjectLib/Panel.razor";

	[Fact]
	public async Task WhenGettingTheBodyOfAnInjectProperty_ThenTheDirectiveLineIsReturned()
	{
		string result = await BodyAsync("RazorInjectLib.Panel.Clock");

		Assert.Contains($"path={PanelRelativePath}\n", result);
		Assert.Contains("loc=1:1-1:28\n", result);
		Assert.Contains("@inject IClockService Clock", Body(result));
		Assert.DoesNotContain("_razor.g.cs", result);
		Assert.DoesNotContain("[global::Microsoft.AspNetCore.Components.InjectAttribute]", result);
		Assert.DoesNotContain("generated=Y", result);
	}

	[Fact]
	public async Task WhenGettingTheBodyOfBuildRenderTree_ThenTheMappedRazorMarkupIsReturned()
	{
		string result = await BodyAsync("RazorInjectLib.Panel.BuildRenderTree");

		Assert.Contains($"path={PanelRelativePath}\n", result);
		Assert.Contains("loc=4:1-5:", result);
		Assert.Contains("<p>@Clock.Now</p>", Body(result));
		Assert.Contains("<p>Count: @Count</p>", Body(result));
		Assert.DoesNotContain("__builder", result);
		Assert.DoesNotContain("_razor.g.cs", result);
	}

	[Fact]
	public async Task WhenGettingTheBodyOfACodeBlockMember_ThenItsRazorSourceIsUnchanged()
	{
		string result = await BodyAsync("RazorInjectLib.Panel.Bump");

		Assert.Contains($"path={PanelRelativePath}\n", result);
		Assert.Contains("private void Bump()", Body(result));
		Assert.Contains("Count++;", Body(result));
		Assert.DoesNotContain("_razor.g.cs", result);
	}

	[Fact]
	public async Task WhenListingTheMembersOfARazorComponent_ThenTheInjectPropertyIsUnderTheRazorFile()
	{
		string result = await MembersAsync("RazorInjectLib.Panel");

		Assert.DoesNotContain("_razor.g.cs", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('	') == "Panel.razor");
		Assert.Contains(result.Split('\n'), line => line.TrimStart('	') == "property,Clock,1:1-1:28");
		Assert.Contains(result.Split('\n'), line => line.TrimStart('	') == "method,get_Clock,1:1-1:28");
		Assert.Contains(result.Split('\n'), line => line.TrimStart('	') == "method,set_Clock,1:1-1:28,IClockService");
	}

	[Fact]
	public async Task WhenGettingTheBodyOfAGeneratedNonRazorDeclaration_ThenItIsStillMarkedGenerated()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx.ReadValue2");

		Assert.Contains("generated=Y\n", result);
		Assert.Contains("generator=GeneratorLib.ShapeGenerator\n", result);
		Assert.Contains("public int ReadValue2() => 2;", result);
	}

	private static async Task<string> BodyAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.RazorInject);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetSymbolBody(TestSolutions.RazorInject, symbolName);
	}

	private static async Task<string> MembersAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.RazorInject);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		return await subject.GetMembers(TestSolutions.RazorInject, symbolName);
	}

	/// <summary>The body after the blank line separating it from the headers.</summary>
	private static string Body(string result)
	{
		int blank = result.IndexOf("\n\n", StringComparison.Ordinal);
		return blank < 0 ? result : result[(blank + 2)..];
	}
}
