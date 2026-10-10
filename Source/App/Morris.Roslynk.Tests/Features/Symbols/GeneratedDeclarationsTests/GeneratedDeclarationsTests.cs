using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.References.FindReads;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Features.Symbols.GetTypeHierarchy;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GeneratedDeclarationsTests;

/// <summary>
/// Every name-based tool resolves a generator's declarations even when no hand-written code of the
/// declaring project mentions the name (the issue #81 shape), including nested generated types and local
/// functions inside generated members.
/// </summary>
public class GeneratedDeclarationsTests
{
	[Fact]
	public async Task WhenAGeneratedMemberIsNotMentionedByHandWrittenCode_ThenGetSymbolBodyReturnsItsBody()
	{
		string result = await WithToolAsync(async registry =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx.ReadValue2"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("public int ReadValue2() => 2;", result);
		Assert.Contains("generated=Y\n", result);
		Assert.Contains("generator=GeneratorLib.ShapeGenerator\n", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberIsNotMentionedByHandWrittenCode_ThenFindReferencesResolvesItWithNoReferences()
	{
		string result = await WithToolAsync(async registry =>
			await new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService())
				.FindReferences(TestSolutions.Generator, "ConsumerLib.Ctx.ReadValue2"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=ConsumerLib.Ctx.ReadValue2()", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberIsCalledOnlyByGeneratedCode_ThenGetCallersListsTheGeneratedCaller()
	{
		string result = await WithToolAsync(async registry =>
			await new GetCallersTool(registry, new SymbolResolver(), new ProjectionService())
				.GetCallers(TestSolutions.Generator, "ConsumerLib.Ctx.Helper"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("CallsHelper", result);
		Assert.Contains("generated=Y", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberHasNoCallers_ThenGetCallersResolvesIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetCallersTool(registry, new SymbolResolver(), new ProjectionService())
				.GetCallers(TestSolutions.Generator, "ConsumerLib.Ctx.ReadValue2"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=ConsumerLib.Ctx.ReadValue2()", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberIsNotMentioned_ThenGetCalleesResolvesIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService())
				.GetCallees(TestSolutions.Generator, "ConsumerLib.Ctx.CallsHelper"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("Helper", result);
	}

	[Fact]
	public async Task WhenAGeneratedOnlyTypeIsNotMentioned_ThenGetTypeHierarchyResolvesIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetTypeHierarchyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetTypeHierarchy(TestSolutions.Generator, "ConsumerLib.OnlyGenerated"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("OnlyGenerated", result);
	}

	[Fact]
	public async Task WhenAGeneratedFieldIsNotMentioned_ThenFindReadsResolvesIt()
	{
		string result = await WithToolAsync(async registry =>
			await new FindReadsTool(registry, new SymbolResolver(), new ProjectionService())
				.FindReads(TestSolutions.Generator, "ConsumerLib.OnlyGenerated.Answer"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=ConsumerLib.OnlyGenerated.Answer", result);
	}

	[Fact]
	public async Task WhenANestedGeneratedTypeIsNotMentioned_ThenGetMembersListsIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetMembersTool(registry, new SymbolResolver(), new ProjectionService())
				.GetMembers(TestSolutions.Generator, "ConsumerLib.Ctx.Cache"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("TryResolve", result);
		Assert.Contains("Ctx.g.cs,generated=Y", result);
	}

	[Fact]
	public async Task WhenANestedGeneratedMemberIsNotMentioned_ThenGetSymbolResolvesIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbol(TestSolutions.Generator, "ConsumerLib.Ctx.Cache.TryResolve"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("generated=Y\n", result);
	}

	[Fact]
	public async Task WhenALocalFunctionLivesInAnUnmentionedGeneratedMember_ThenGetSymbolBodyReturnsIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx.Outer.Inner"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("static int Inner() => 41;", result);
	}

	[Fact]
	public async Task WhenAPartialTypeHasAGeneratedHalf_ThenGetSymbolBodyReturnsBothParts()
	{
		string result = await WithToolAsync(async registry =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx"));

		Assert.Contains("parts=2\n", result);
		Assert.Contains("path=ConsumerLib/Ctx.cs", result);
		string[] partLines = result.Split('\n').Where(line => line.StartsWith("part=", StringComparison.Ordinal)).ToArray();
		Assert.Equal(2, partLines.Length);
		Assert.DoesNotContain("generated=", partLines[0]);
		Assert.Contains("generated=Y", partLines[1]);
	}

	[Fact]
	public async Task WhenGetSymbolBodyIsGivenANestedMetadataName_ThenItReturnsTheSourceBody()
	{
		// A metadata-spelled name ('+'-joined) misses resolution entirely, but the symbol does have source
		// declarations: get_symbol_body builds the body from them instead of a false NotSupported.
		string result = await WithToolAsync(async registry =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx+Cache"));

		Assert.DoesNotContain("error=", result);
		Assert.Contains("public sealed class Cache", result);
		Assert.Contains("generated=Y\n", result);
	}

	[Fact]
	public async Task WhenAGeneratedMemberNameIsIncomplete_ThenNotFoundSuggestsIt()
	{
		string result = await WithToolAsync(async registry =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Ctx.ReadValue"));

		Assert.Contains("error=NotFound", result);
		Assert.Contains("ConsumerLib.Ctx.ReadValue2", result);
	}

	private static async Task<string> WithToolAsync(Func<InstanceRegistry, Task<string>> run)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		return await run(registry);
	}
}
