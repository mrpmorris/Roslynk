using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GeneratedLocationsTests;

public class GeneratedLocationsTests
{
	[Fact]
	public async Task WhenAPartialMethodHasAnImplementationPart_ThenGetSymbolBodyReturnsBothParts()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Widget.Compute"));

		Assert.Contains("parts=2\n", result);
		Assert.Contains("path=ConsumerLib/Widget.Declaration.cs", result);
		Assert.Contains("path=ConsumerLib/Widget.Implementation.cs", result);
		Assert.Contains("public partial int Compute(int value);", result);
		Assert.Contains("=> value * 2;", result);
		Assert.DoesNotContain("generated=", result);
	}

	[Fact]
	public async Task WhenAGeneratorImplementsAPartialMethod_ThenTheGeneratedPartIsReturnedAndMarked()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "ConsumerLib.Patterns.Letters"));

		Assert.Contains("parts=2\n", result);
		Assert.Contains("path=ConsumerLib/Patterns.cs", result);

		string[] partLines = result.Split('\n').Where(line => line.StartsWith("part=", StringComparison.Ordinal)).ToArray();
		Assert.Equal(2, partLines.Length);
		Assert.DoesNotContain("generated=", partLines[0]);
		Assert.Contains("generated=Y", partLines[1]);
		Assert.Contains("generator=", partLines[1]);
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredByASourceGenerator_ThenGetSymbolMarksItsLocationGenerated()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbol(TestSolutions.Generator, "GeneratedNamespace.Hello"));

		Assert.Contains("generated=Y\n", result);
		Assert.Contains("generator=GeneratorLib.HelloGenerator\n", result);
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredByASourceGenerator_ThenGetSymbolBodyMarksItsLocationGenerated()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Generator, "GeneratedNamespace.Hello"));

		Assert.Contains("generated=Y\n", result);
		Assert.Contains("generator=GeneratorLib.HelloGenerator\n", result);
		Assert.Contains("Greeting", result);
	}

	[Fact]
	public async Task WhenASymbolIsDeclaredInARegularFile_ThenItsLocationIsNotMarkedGenerated()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbol(TestSolutions.Generator, "ConsumerLib.UsesGenerated"));

		Assert.Contains("path=ConsumerLib/UsesGenerated.cs", result);
		Assert.DoesNotContain("generated", result);
	}

	[Fact]
	public async Task WhenSearchingForASymbolInARegularFile_ThenItsFileNodeIsNotMarked()
	{
		string result = await WithToolAsync(async (registry) =>
			await new SearchSymbolsTool(registry, new ProjectionService())
				.SearchSymbols(TestSolutions.Generator, "HelloGenerator"));

		Assert.Contains("HelloGenerator.cs", result);
		Assert.DoesNotContain("generated=Y", result);
	}

	[Fact]
	public async Task WhenListingMembersOfAGeneratedType_ThenItsFileNodeIsMarkedGenerated()
	{
		string result = await WithToolAsync(async (registry) =>
			await new GetMembersTool(registry, new SymbolResolver(), new ProjectionService())
				.GetMembers(TestSolutions.Generator, "GeneratedNamespace.Hello"));

		Assert.Contains("Hello.g.cs,generated=Y", result);
	}

	[Fact]
	public async Task WhenReferencesAreInARegularFile_ThenTheirFileNodeIsNotMarked()
	{
		string result = await WithToolAsync(async (registry) =>
			await new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService())
				.FindReferences(TestSolutions.Generator, "GeneratedNamespace.Hello"));

		Assert.Contains("UsesGenerated.cs", result);
		Assert.DoesNotContain("generated=Y", result);
	}

	private static async Task<string> WithToolAsync(Func<InstanceRegistry, Task<string>> run)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		return await run(registry);
	}
}
