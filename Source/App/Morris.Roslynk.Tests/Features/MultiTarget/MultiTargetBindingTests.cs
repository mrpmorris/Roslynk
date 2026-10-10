using Microsoft.CodeAnalysis;
using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.Signatures.RenameParameter;
using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.MultiTarget;

/// <summary>
/// The MultiTarget fixture's <c>Legacy</c> member binds only in netstandard2.0 (its <c>using</c> sits under
/// <c>#if !NET8_0_OR_GREATER</c>), so the copy discovered first — net8.0, per the fixture's TFM order — is
/// the one whose parameter types fail to bind. That is the load-order dependence the best-bound
/// representative rule removes: every discriminating test keeps the fixture's own precondition as a guard,
/// so a future TFM-order or Roslyn change cannot make them pass vacuously.
/// </summary>
public class MultiTargetBindingTests
{
	[Fact]
	public async Task WhenTheFirstCopyCannotBindAParameterType_ThenResolvedSymbolUsesTheBoundRendering()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		await FirstDiscoveredCopyMustBeUnboundAsync(instance, "Repro.Legacy.Wide");
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.MultiTarget, "Repro.Legacy.Wide");

		Assert.Contains("resolvedSymbol=Repro.Legacy.Wide(int)", result);
		Assert.DoesNotContain("Repro.Legacy.Wide(Int32)", result);
	}

	[Fact]
	public async Task WhenTheFirstCopyCannotBindAParameterType_ThenGetMembersListsTheBoundParameterTypes()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		await FirstDiscoveredCopyMustBeUnboundAsync(instance, "Repro.Legacy.Wide");
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.MultiTarget, "Repro.Legacy");

		Assert.Equal(1, CountOccurrences(result, "method,Wide,"));
		Assert.Equal(2, CountOccurrences(result, "method,Size,"));
		Assert.DoesNotContain("Int32", result);
	}

	[Fact]
	public async Task WhenAnOverloadedCalleeCannotBindInTheFirstCopy_ThenGetCalleesListsTheBoundParameterTypes()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		await FirstDiscoveredCopyMustBeUnboundAsync(instance, "Repro.Legacy.Wide");
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(TestSolutions.MultiTarget, "Repro.Legacy.Caller");

		// Legacy declares Size twice, so the Size callee leaf carries its parameter list — from the bound copy.
		string sizeLine = result.Split('\r', '\n').Single(line => line.Contains("method,Size,"));
		Assert.EndsWith(",int", sizeLine);
		Assert.DoesNotContain("Int32", result);
	}

	[Fact]
	public async Task WhenTheEchoedNameIsSentBack_ThenItResolvesToTheSameSymbol()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		// The bound rendering the tools now echo...
		string bound = await subject.GetSymbolBody(TestSolutions.MultiTarget, "Repro.Legacy.Wide(int)");
		Assert.DoesNotContain("error=", bound);
		Assert.Contains("path=", bound);

		// ...and the degraded spelling still resolves (through the copy where it is written), so the fix
		// does not narrow what a name reaches.
		string degraded = await subject.GetSymbolBody(TestSolutions.MultiTarget, "Repro.Legacy.Wide(Int32)");
		Assert.DoesNotContain("error=", degraded);
		Assert.Contains("path=", degraded);
	}

	[Fact]
	public async Task WhenOverloadsCollideOnlyAtTheErrorSpelling_ThenCandidatesUseTheBoundTypes()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		await FirstDiscoveredCopyMustBeUnboundAsync(instance, "Repro.Legacy.Wide");
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.MultiTarget, "Repro.Legacy.Size");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=Repro.Legacy.Size(int)", result);
		Assert.Contains("candidate=Repro.Legacy.Size(string)", result);
	}

	[Fact]
	public async Task WhenEveryTargetFrameworkBindsTheParameterTypes_ThenEchoedNameIsUnchanged()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		// Both copies of Echo bind, so discovery order still decides and the echo is byte-for-byte stable.
		string echo = await subject.FindReferences(TestSolutions.MultiTarget, "Repro.Api.Echo");
		Assert.Contains("resolvedSymbol=Repro.Api.Echo(string)", echo);

		// The issue's fixture case: the bound copy's Minimal rendering of System.Type is 'Type' — the echo
		// names the bound copy either way, and no tier escalation happens.
		string accepts = await subject.FindReferences(TestSolutions.MultiTarget, "Repro.Api.Accepts");
		Assert.Contains("resolvedSymbol=Repro.Api.Accepts(Type)", accepts);
	}

	[Fact]
	public async Task WhenTheFirstDocumentCannotBindTheParameterType_ThenFindDefinitionReportsTheBoundName()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		await FirstDiscoveredCopyMustBeUnboundAsync(instance, "Repro.Legacy.Wide");
		var subject = new FindDefinitionTool(registry, new SymbolResolver(), new ProjectionService());

		string sourcePath = Path.Combine(Path.GetDirectoryName(TestSolutions.MultiTarget)!, "Multi", "Legacy.cs");
		(int declarationLine, int declarationColumn, int typeLine, int typeColumn) = Locate(sourcePath, "Wide(Int32");

		// The declaration token: net8.0's copy is found first but its parameter is unresolved, so the bound
		// copy from the second TFM must speak.
		string declaration = await subject.FindDefinition(TestSolutions.MultiTarget, sourcePath, declarationLine, declarationColumn);
		Assert.Contains("fullName=Repro.Legacy.Wide(int)", declaration);

		// The type token itself does not bind in net8.0 at all; it resolves through the second TFM.
		string type = await subject.FindDefinition(TestSolutions.MultiTarget, sourcePath, typeLine, typeColumn);
		Assert.Contains("fullName=System.Int32", type);
	}

	[Fact]
	public async Task WhenTheNameSpellsTheBoundType_ThenRenameParameterCoversEveryCopy()
	{
		// A bare name matches every copy, so the previewed rename must reach the declaration in both
		// projections — representative selection narrows nothing. (A parameterized spelling matches only the
		// copies its parameter types bind in, so it would rename one.)
		string solutionPath = TestSolutions.CreateScratchMultiTargetSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, "Repro.Legacy.Wide", "value", "number", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("renamedMembers=2", result);
		Assert.DoesNotContain("error=", result);
	}

	/// <summary>
	/// The precondition the discriminating tests rest on: the copy discovered first for
	/// <paramref name="name"/> is the one whose parameter type failed to bind.
	/// </summary>
	private static async Task FirstDiscoveredCopyMustBeUnboundAsync(RoslynInstance instance, string name)
	{
		ISymbol first = (await new SymbolResolver().FindByFullyQualifiedNameAsync(instance.CurrentSolution, name))[0];
		Assert.True(
			ErrorTypeCount.Of(first) > 0,
			$"The fixture must discover the unbound copy of '{name}' first, or this test proves nothing.");
	}

	private static (int DeclarationLine, int DeclarationColumn, int TypeLine, int TypeColumn) Locate(string path, string pattern)
	{
		string[] lines = File.ReadAllLines(path);
		for (int index = 0; index < lines.Length; index++)
		{
			int patternColumn = lines[index].IndexOf(pattern, StringComparison.Ordinal);
			if (patternColumn >= 0)
			{
				// 1-based line and column of the declaring identifier and of the unresolved type after it.
				return (index + 1, patternColumn + 1, index + 1, lines[index].IndexOf("Int32", patternColumn, StringComparison.Ordinal) + 1);
			}
		}

		throw new InvalidOperationException($"'{pattern}' was not found in {path}.");
	}

	private static int CountOccurrences(string text, string value)
	{
		int count = 0;
		for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
			count++;

		return count;
	}
}
