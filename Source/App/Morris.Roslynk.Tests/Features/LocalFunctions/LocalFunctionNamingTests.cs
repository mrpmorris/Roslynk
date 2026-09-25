using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Features.Refactorings.ExtractMethod;
using Morris.Roslynk.Features.Signatures.ChangeSignature;
using Morris.Roslynk.Features.Signatures.RenameParameter;
using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Features.MultiQuery;

namespace Morris.Roslynk.Tests.Features.LocalFunctions;

/// <summary>
/// Local functions are named as members of the member declaring them, through a nested class and through a
/// local function declared inside another: LocalFunctionLib.ParentClass.Widget.Method1.localMethod.inner.
/// Every name-based tool resolves that form, and every name a tool emits for a local function is one it accepts.
/// </summary>
public class LocalFunctionNamingTests
{
	private const string Widget = "LocalFunctionLib.ParentClass.Widget";
	private const string LocalMethod = Widget + ".Method1.localMethod";
	private const string Inner = LocalMethod + ".inner";
	private const string LocalMethodSignature = Widget + ".Method1(int).localMethod(string, int)";
	private const string InnerSignature = LocalMethodSignature + ".inner(string)";
	private const string FixtureFile = "LocalFunctionLib/ParentClass.cs";

	// ---- naming and resolution ----

	[Fact]
	public async Task WhenNamingALocalFunctionInsideALocalFunctionOfANestedClass_ThenEveryContainerIsQualified()
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);
		IMethodSymbol inner = await ResolveSingleAsync(solution, Inner);

		Assert.Equal(MethodKind.LocalFunction, inner.MethodKind);
		Assert.Equal(Inner, SymbolResolver.FullyQualifiedName(inner));
		Assert.Equal(InnerSignature, SymbolResolver.SignatureName(inner));
		Assert.Equal("localfunction", SymbolKindText.Of(inner));
	}

	[Theory]
	[InlineData(LocalMethod)]
	[InlineData(Widget + ".Method1(int).localMethod")]
	[InlineData(Widget + ".Method1.localMethod(string, int)")]
	[InlineData(LocalMethodSignature)]
	[InlineData(Widget + ".Method1(int count).localMethod(string p1, int p2)")]
	public async Task WhenResolvingALocalFunctionWithOrWithoutParameterLists_ThenItResolves(string name)
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);

		IMethodSymbol local = await ResolveSingleAsync(solution, name);

		Assert.Equal("localMethod", local.Name);
		Assert.Equal(LocalMethodSignature, SymbolResolver.SignatureName(local));
	}

	[Theory]
	[InlineData(Inner)]
	[InlineData(InnerSignature)]
	[InlineData(Widget + ".Method1(int).localMethod.inner(string)")]
	public async Task WhenResolvingALocalFunctionNestedInALocalFunction_ThenItResolves(string name)
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);

		IMethodSymbol inner = await ResolveSingleAsync(solution, name);

		Assert.Equal("inner", inner.Name);
		Assert.Equal("localMethod", ((IMethodSymbol)inner.ContainingSymbol).Name);
	}

	[Theory]
	[InlineData(Widget + ".Method1(string).localMethod")]
	[InlineData(Widget + ".Method1.localMethod(int)")]
	[InlineData(Widget + ".Method1.inner")]
	[InlineData(Widget + ".localMethod")]
	[InlineData(Widget + ".Method1.missing")]
	public async Task WhenTheContainerChainOrSignatureDoesNotMatch_ThenNothingResolves(string name)
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);

		IReadOnlyList<ISymbol> matches = await new SymbolResolver().FindByFullyQualifiedNameAsync(solution, name);

		Assert.Empty(matches);
	}

	[Fact]
	public async Task WhenOverloadsOfTheContainerDeclareTheSameLocalName_ThenTheCandidatesQualifyTheContainerAndRoundTrip()
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);
		var resolver = new SymbolResolver();

		IReadOnlyList<ISymbol> matches = await resolver.FindByFullyQualifiedNameAsync(solution, Widget + ".Method2.helper");
		IReadOnlyList<string> candidates = SymbolSignature.Distinguish(matches);

		Assert.Equal([Widget + ".Method2(int).helper(int)", Widget + ".Method2(string).helper(string)"], candidates);
		foreach (string candidate in candidates)
		{
			IReadOnlyList<ISymbol> resolved = await resolver.FindByFullyQualifiedNameAsync(solution, candidate);
			Assert.Equal(candidate, SymbolResolver.SignatureName(Assert.Single(resolved)));
		}
	}

	[Fact]
	public async Task WhenResolvingABareLocalFunctionName_ThenItIsFound()
	{
		Solution solution = await LoadAsync(TestSolutions.LocalFunctions);

		IMethodSymbol inner = await ResolveSingleAsync(solution, "inner");

		Assert.Equal(InnerSignature, SymbolResolver.SignatureName(inner));
	}

	// ---- read tools ----

	[Fact]
	public async Task WhenGettingTheBodyOfALocalFunctionInsideALocalFunction_ThenItsSourceIsReturnedVerbatim()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbolBody(TestSolutions.LocalFunctions, Inner);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("static int inner(string text)\r\n\t\t\t\t{\r\n\t\t\t\t\treturn text.Length;\r\n\t\t\t\t}", result);
	}

	[Fact]
	public async Task WhenGettingALocalFunctionSymbol_ThenItsKindAndDeclarationAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbol(TestSolutions.LocalFunctions, LocalMethod);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("static int localMethod(string p1, int p2)", result);
	}

	[Fact]
	public async Task WhenAnAmbiguousLocalFunctionIsRequested_ThenTheCandidatesAreTheQualifiedOverloads()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetSymbolBody(TestSolutions.LocalFunctions, Widget + ".Method2.helper");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains($"candidate={Widget}.Method2(int).helper(int)", result);
		Assert.Contains($"candidate={Widget}.Method2(string).helper(string)", result);
	}

	[Fact]
	public async Task WhenFindingReferencesToANestedLocalFunction_ThenTheReferenceNestsUnderItsContainingLocalFunction()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.LocalFunctions, Inner);

		Assert.Contains($"resolvedSymbol={InnerSignature}", result);
		string[] lines = result.Split('\n');
		int method = Array.FindIndex(lines, line => line.Trim().StartsWith("method,Method1", StringComparison.Ordinal));
		int local = Array.FindIndex(lines, line => line.Trim().StartsWith("localfunction,localMethod", StringComparison.Ordinal));
		Assert.True(method >= 0 && local > method, result);
		Assert.Contains("13:", lines[local]);
	}

	[Fact]
	public async Task WhenGettingTheCallersOfANestedLocalFunction_ThenTheCallingLocalFunctionNestsUnderItsMethod()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetCallersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallers(TestSolutions.LocalFunctions, Inner);

		Assert.Contains($"resolvedSymbol={InnerSignature}", result);
		Assert.Contains("class,ParentClass", result);
		Assert.Contains("class,Widget", result);
		Assert.Contains("method,Method1", result);
		Assert.Contains("localfunction,localMethod,11:", result);
	}

	[Fact]
	public async Task WhenSearchingForALocalFunctionName_ThenTheLocalFunctionIsListedUnderItsContainers()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new SearchSymbolsTool(registry, new ProjectionService());

		string result = await subject.SearchSymbols(TestSolutions.LocalFunctions, "inner");

		Assert.Contains("localfunction,localMethod", result);
		Assert.Contains("localfunction,inner,16:", result);
	}

	[Fact]
	public async Task WhenFindingTheDefinitionOfANestedLocalFunctionCall_ThenItsFullNameIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new FindDefinitionTool(registry, new SymbolResolver(), new ProjectionService());

		// Line 13 is 'int length = inner(p1);'.
		string result = await subject.FindDefinition(TestSolutions.LocalFunctions, FixtureFile, 13, 19);

		Assert.Contains($"fullName={InnerSignature}", result);
		Assert.Contains("kind=localfunction", result);
	}

	[Fact]
	public async Task WhenListingTheMembersOfANestedClass_ThenEachMembersLocalFunctionsNestBeneathIt()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var subject = new GetMembersTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetMembers(TestSolutions.LocalFunctions, Widget);

		Assert.Equal(
			$"resolvedType={Widget}\n" +
			"\n" +
			"LocalFunctionLib\n" +
			"\tLocalFunctionLib\n" +
			"\t\tParentClass.cs\n" +
			"\t\t\tmethod,Method1,7:3-21:4,int\n" +
			"\t\t\t\tlocalfunction,localMethod,11:4-20:5,string|int\n" +
			"\t\t\t\t\tlocalfunction,inner,16:5-19:6,string\n" +
			"\t\t\tmethod,Method2,23:3-28:4,int\n" +
			"\t\t\t\tlocalfunction,helper,27:4-27:48,int\n" +
			"\t\t\tmethod,Method2,30:3-35:4,string\n" +
			"\t\t\t\tlocalfunction,helper,34:4-34:50,string\n" +
			"\t\t\tmethod,Caller,37:3-37:50\n",
			result.Replace("\r\n", "\n"));
	}

	[Fact]
	public async Task WhenBatchingQueriesOnLocalFunctions_ThenEverySlotResolves()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.LocalFunctions);
		var provider = new ServiceCollection()
			.AddSingleton(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.MultiQuery(
			TestSolutions.LocalFunctions,
			[
				new(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", Json(Inner)))),
				new(MultiQueryOp.find_references, MultiQueryTestHelpers.Args(("symbolName", Json(LocalMethodSignature)))),
				new(MultiQueryOp.get_callers, MultiQueryTestHelpers.Args(("methodName", Json(LocalMethod)))),
			]);

		Assert.DoesNotContain("error=", envelope);
		Assert.Contains("return text.Length;", envelope);
		Assert.Contains($"resolvedSymbol={LocalMethodSignature}", envelope);
	}

	// ---- write tools ----

	[Fact]
	public async Task WhenRenamingAParameterOfALocalFunctionInANestedClass_ThenTheDeclarationAndUsesAreRenamed()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, LocalMethod, "p1", "value");

		Assert.Contains("applied=Y", result);
		Assert.Contains($"resolvedMethod={LocalMethodSignature}", result);
		string text = await ReadAsync(solutionPath);
		Assert.Contains("static int localMethod(string value, int p2)", text);
		Assert.Contains("int length = inner(value);", text);
		Assert.Contains("static int inner(string text)", text);
	}

	[Fact]
	public async Task WhenRenamingAParameterOfALocalFunctionInsideALocalFunction_ThenOnlyThatFunctionChanges()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, Inner, "text", "word");

		Assert.Contains("applied=Y", result);
		string text = await ReadAsync(solutionPath);
		Assert.Contains("static int inner(string word)", text);
		Assert.Contains("return word.Length;", text);
		Assert.Contains("static int helper(string text) => text.Length;", text);
	}

	[Fact]
	public async Task WhenRenamingAParameterOfAnAmbiguousLocalFunction_ThenTheOverloadCandidatesAreReturned()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());
		string before = await ReadAsync(solutionPath);

		string ambiguous = await subject.RenameParameter(solutionPath, Widget + ".Method2.helper", "text", "value");
		string targeted = await subject.RenameParameter(solutionPath, Widget + ".Method2(string).helper(string)", "text", "value");

		Assert.Contains("error=Ambiguous", ambiguous);
		Assert.Contains("applied=Y", targeted);
		string text = await ReadAsync(solutionPath);
		Assert.NotEqual(before, text);
		Assert.Contains("static int helper(string value) => value.Length;", text);
		Assert.Contains("static int helper(int number) => number * 2;", text);
	}

	[Fact]
	public async Task WhenRenamingALocalFunctionInsideALocalFunction_ThenItsDeclarationAndCallAreRenamed()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, Inner, "Measure");

		Assert.Contains("applied=Y", result);
		string text = await ReadAsync(solutionPath);
		Assert.Contains("int length = Measure(p1);", text);
		Assert.Contains("static int Measure(string text)", text);
		Assert.DoesNotContain("inner", text);
	}

	[Fact]
	public async Task WhenAddingAParameterToALocalFunctionInsideALocalFunction_ThenItsCallSiteIsUpdated()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(solutionPath, Inner, "int", "factor", "1", callSiteArgument: "2");

		Assert.Contains("applied=Y", result);
		Assert.Contains("updatedCallSites=1", result);
		Assert.Contains($"resolvedMethod={InnerSignature}", result);
		string text = await ReadAsync(solutionPath);
		Assert.Contains("static int inner(string text, int factor = 1)", text);
		Assert.Contains("int length = inner(p1, factor: 2);", text);
	}

	[Fact]
	public async Task WhenExtractingALocalFunctionFromALocalFunctionInsideALocalFunction_ThenItsFullNameIsReported()
	{
		string solutionPath = TestSolutions.CreateScratchLocalFunctionSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());

		// Line 18 is '\t\t\t\t\treturn text.Length;'; select the expression 'text.Length'.
		string result = await subject.ExtractMethod(solutionPath, FixtureFile, 18, 13, 18, 24, methodName: "LengthOf", asLocalFunction: true);

		Assert.Contains("applied=Y", result);
		Assert.Contains("kind=LocalFunction", result);
		Assert.Contains($"symbolName={InnerSignature}.LengthOf(string)", result);

		// The reported name resolves straight back to the new local function.
		Solution solution = registry.GetOrBegin(solutionPath).CurrentModel.Solution!;
		IMethodSymbol extracted = await ResolveSingleAsync(solution, $"{Inner}.LengthOf");
		Assert.Equal(MethodKind.LocalFunction, extracted.MethodKind);
	}

	// ---- helpers ----

	private static async Task<Solution> LoadAsync(string solutionPath)
	{
		var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		return instance.CurrentModel.Solution!;
	}

	private static async Task<IMethodSymbol> ResolveSingleAsync(Solution solution, string name)
	{
		IReadOnlyList<ISymbol> matches = await new SymbolResolver().FindByFullyQualifiedNameAsync(solution, name);
		return Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(matches));
	}

	private static Task<string> ReadAsync(string solutionPath) =>
		File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(solutionPath)!, "LocalFunctionLib", "ParentClass.cs"));

	private static JsonElement Json(string value) => MultiQueryTestHelpers.Json(value);
}
