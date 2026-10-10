using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// Declarations only a generator emits (no hand-written document of the declaring project mentions their
/// name) resolve anyway: the resolver asks the compilation of generator-bearing projects when Roslyn's
/// declaration search — which pre-filters on a project's regular documents — comes up empty, and for bare
/// names so a hand-written hit elsewhere cannot hide a generated candidate.
/// </summary>
public class GeneratedDeclarationResolutionTests
{
	[Fact]
	public async Task WhenAGeneratedMemberIsNotMentionedByHandWrittenCode_ThenItResolves()
	{
		(Solution solution, IReadOnlyList<ISymbol> matches) = await ResolveAsync("ConsumerLib.Ctx.ReadValue2");

		ISymbol match = Assert.Single(matches);
		Assert.True(GeneratedSource.IsGenerated(solution, match.DeclaringSyntaxReferences[0].SyntaxTree));
	}

	[Fact]
	public async Task WhenANestedGeneratedTypeIsNotMentioned_ThenItsMemberResolves()
	{
		// Broken before the generated pass: the declaration search misses the nested type and the metadata
		// fallback needs a '+'-joined name, which 'ConsumerLib.Ctx.Cache' is not.
		(Solution _, IReadOnlyList<ISymbol> matches) = await ResolveAsync("ConsumerLib.Ctx.Cache.TryResolve");

		Assert.Single(matches);
	}

	[Fact]
	public async Task WhenATypeExistsOnlyInGeneratedCode_ThenItResolves()
	{
		(Solution _, IReadOnlyList<ISymbol> matches) = await ResolveAsync("ConsumerLib.OnlyGenerated");

		ISymbol match = Assert.Single(matches);
		Assert.IsAssignableFrom<INamedTypeSymbol>(match);
	}

	[Fact]
	public async Task WhenALocalFunctionLivesInAnUnmentionedGeneratedMember_ThenItResolves()
	{
		(Solution _, IReadOnlyList<ISymbol> matches) = await ResolveAsync("ConsumerLib.Ctx.Outer.Inner");

		ISymbol match = Assert.Single(matches);
		IMethodSymbol local = Assert.IsAssignableFrom<IMethodSymbol>(match);
		Assert.Equal(MethodKind.LocalFunction, local.MethodKind);
		Assert.Equal("ConsumerLib.Ctx.Outer().Inner()", SymbolSignature.Of(local));
	}

	[Fact]
	public async Task WhenABareLocalFunctionNameIsDeclaredOnlyInGeneratedCode_ThenItResolves()
	{
		// GetSymbolsWithName never returns local functions, so a bare name reaches the local-function scan,
		// which must look inside generated documents too.
		(Solution _, IReadOnlyList<ISymbol> matches) = await ResolveAsync("Inner");

		ISymbol match = Assert.Single(matches);
		Assert.Equal(MethodKind.LocalFunction, ((IMethodSymbol)match).MethodKind);
	}

	[Fact]
	public async Task WhenABareNameMatchesAHandWrittenAndAnUnmentionedGeneratedDeclaration_ThenBothResolve()
	{
		// GeneratorLib.ShapeGenerator.TryResolve is hand-written (in another project, so its name is absent
		// from the declaring project's regular documents) and ConsumerLib.Ctx.Cache.TryResolve is generated.
		(Solution _, IReadOnlyList<ISymbol> matches) = await ResolveAsync("TryResolve");

		Assert.Equal(2, matches.Count);
		Assert.Contains(matches, symbol => symbol.ContainingType?.Name == "Cache");
		Assert.Contains(matches, symbol => symbol.ContainingType?.Name == "ShapeGenerator");
	}

	[Fact]
	public async Task WhenAGeneratedMemberIsMentionedByHandWrittenCode_ThenItResolvesExactlyOnce()
	{
		// Mentioned generated members come from the primary search; the generated pass must not duplicate
		// them (same symbol instance, deduped by the seen set) — qualified and bare alike.
		(Solution _, IReadOnlyList<ISymbol> qualified) = await ResolveAsync("GeneratedNamespace.Hello.Greeting");
		Assert.Single(qualified);

		(Solution _, IReadOnlyList<ISymbol> bare) = await ResolveAsync("Hello");
		Assert.Single(bare);
	}

	[Fact]
	public async Task WhenAGeneratedMemberNameIsIncomplete_ThenItIsSuggested()
	{
		var resolved = await ResolveAsync("ConsumerLib.Ctx.ReadValue");

		IReadOnlyList<string> suggestions = await new SymbolResolver().SuggestAsync(resolved.Solution, "ConsumerLib.Ctx.ReadValue");

		Assert.Contains("ConsumerLib.Ctx.ReadValue2", suggestions);
	}

	[Fact]
	public async Task WhenAGeneratedNameCollidesWithAMetadataSymbol_ThenBothCandidatesAreReported()
	{
		// A metadata symbol of the same simple name is a real candidate (the declaration search's metadata
		// leg is not pre-filtered), so the generated one joining it is Ambiguous at the tool layer rather
		// than a silent pick of either.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		string shapeGenerator = Path.Combine(Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "ShapeGenerator.cs");
		await File.WriteAllTextAsync(
			shapeGenerator,
			(await File.ReadAllTextAsync(shapeGenerator)).Replace("OnlyGenerated", "String", StringComparison.Ordinal));
		TestSolutions.BuildScratchGenerator(solutionPath);

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);

		IReadOnlyList<ISymbol> matches = await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, "String");

		Assert.True(matches.Count >= 2);
		Assert.Contains(matches, symbol => symbol.DeclaringSyntaxReferences.IsEmpty);
		Assert.Contains(matches, symbol => symbol.DeclaringSyntaxReferences.Length > 0
			&& GeneratedSource.IsGenerated(instance.CurrentSolution, symbol.DeclaringSyntaxReferences[0].SyntaxTree));
	}

	private static async Task<(Solution Solution, IReadOnlyList<ISymbol> Matches)> ResolveAsync(string symbolName)
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);

		return (instance.CurrentSolution, await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, symbolName));
	}
}
