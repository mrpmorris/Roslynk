using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Infrastructure.Callers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCallersTests;

/// <summary>
/// The behavior matrix for get_callers: which compiler-inserted calls are attributed to a caller, how the
/// caller is named, and which shapes reach which mechanism. Pins the corrected matrix from issue #62 (the
/// issue's own table was wrong for += / ++ / using Dispose / foreach GetEnumerator / MoveNext / Current,
/// which FindCallersAsync has always reported) and the true gaps the implicit-call scan fills.
/// </summary>
public class GetCallersCoverageTests : IClassFixture<GetCallersCoverageTests.ScratchSolution>
{
	private readonly ScratchSolution Scratch;

	public GetCallersCoverageTests(ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenAUserDefinedOperatorIsUsedInACompoundAssignment_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Money.op_Addition");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Money.op_Addition(Money, Money)", result);
		Assert.Contains("method,Compound,", result);
	}

	[Fact]
	public async Task WhenAUserDefinedIncrementIsUsed_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Money.op_Increment");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,UserOperators,", result);
	}

	[Fact]
	public async Task WhenTheOnlyUseIsAnImplicitConversion_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Money.op_Implicit");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ImplicitConvert,", result);
		Assert.Contains("method,UserOperators,", result);
		Assert.Contains("method,ForEachMoney,", result);
	}

	[Fact]
	public async Task WhenTheOnlyUseIsAnExplicitCast_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Money.op_Explicit");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ExplicitCast,", result);
	}

	[Fact]
	public async Task WhenACollectionIsEnumerated_ThenTheGetEnumeratorMoveNextAndCurrentCallersAreReported()
	{
		string enumerators = await Scratch.GetCallersAsync("SimpleLibrary.Bag.GetEnumerator");
		string moveNext = await Scratch.GetCallersAsync("SimpleLibrary.BagEnumerator.MoveNext");
		string current = await Scratch.GetCallersAsync("SimpleLibrary.BagEnumerator.Current");

		Assert.DoesNotContain("error=", enumerators);
		Assert.Contains("method,ForEachBag,", enumerators);
		Assert.DoesNotContain("error=", moveNext);
		Assert.Contains("method,ForEachBag,", moveNext);
		Assert.DoesNotContain("error=", current);
		Assert.Contains("method,ForEachBag,", current);
	}

	[Fact]
	public async Task WhenAnEnumeratorIsDisposedByTheLoop_ThenTheLoopIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.BagEnumerator.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ForEachBag,", result);
	}

	[Fact]
	public async Task WhenAnAwaitableIsAwaited_ThenGetAwaiterIsCompletedAndGetResultCallersAreReported()
	{
		string getAwaiter = await Scratch.GetCallersAsync("SimpleLibrary.MyAwaitable.GetAwaiter");
		string isCompleted = await Scratch.GetCallersAsync("SimpleLibrary.MyAwaiter.IsCompleted");
		string getResult = await Scratch.GetCallersAsync("SimpleLibrary.MyAwaiter.GetResult");

		Assert.DoesNotContain("error=", getAwaiter);
		Assert.Contains("method,AwaitCustom,", getAwaiter);
		Assert.DoesNotContain("error=", isCompleted);
		Assert.Contains("method,AwaitCustom,", isCompleted);
		Assert.DoesNotContain("error=", getResult);
		Assert.Contains("method,AwaitCustom,", getResult);
	}

	[Fact]
	public async Task WhenAUsingDeclarationDisposesAResource_ThenTheUsingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Resource2.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,UsesResource,", result);
	}

	[Fact]
	public async Task WhenALocalOfADisposableTypeIsDeclaredWithoutUsing_ThenItIsNotACallerOfDispose()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Resource2.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,DeclaresDisposable,", result);
	}

	[Fact]
	public async Task WhenAMethodAlsoReallyDisposes_ThenItIsStillReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Resource2.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,AlsoReallyDisposes,", result);
	}

	[Fact]
	public async Task WhenAnAwaitUsingDisposesAsync_ThenTheAwaitUsingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.AsyncResource2.DisposeAsync");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,AwaitUsingAsync,", result);
	}

	[Fact]
	public async Task WhenAnAwaitForEachEnumerates_ThenTheAsyncPlumbingCallersAreReported()
	{
		string getAsyncEnumerator = await Scratch.GetCallersAsync("SimpleLibrary.AsyncBag.GetAsyncEnumerator");
		string moveNextAsync = await Scratch.GetCallersAsync("SimpleLibrary.AsyncBagEnumerator.MoveNextAsync");
		string disposeAsync = await Scratch.GetCallersAsync("SimpleLibrary.AsyncBagEnumerator.DisposeAsync");

		Assert.DoesNotContain("error=", getAsyncEnumerator);
		Assert.Contains("method,AwaitForeachBag,", getAsyncEnumerator);
		Assert.DoesNotContain("error=", moveNextAsync);
		Assert.Contains("method,AwaitForeachBag,", moveNextAsync);
		Assert.DoesNotContain("error=", disposeAsync);
		Assert.Contains("method,AwaitForeachBag,", disposeAsync);
	}

	[Fact]
	public async Task WhenAConditionUsesATrueOperator_ThenTheConditionalMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Flag.op_True");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Flag.op_True(Flag)", result);
		Assert.Contains("method,Condition,", result);
	}

	[Fact]
	public async Task WhenAQueryExpressionIsUsed_ThenTheQueryMethodsCallerIsReported()
	{
		string where = await Scratch.GetCallersAsync("SimpleLibrary.QuerySource.Where");
		string select = await Scratch.GetCallersAsync("SimpleLibrary.QuerySource.Select");

		Assert.DoesNotContain("error=", where);
		Assert.Contains("method,Queries,", where);
		Assert.DoesNotContain("error=", select);
		Assert.Contains("method,Queries,", select);
	}

	[Fact]
	public async Task WhenAnInterpolatedStringTargetsAHandler_ThenTheHandlerMembersCallersAreReported()
	{
		string appendLiteral = await Scratch.GetCallersAsync("SimpleLibrary.Handler.AppendLiteral");
		string appendFormatted = await Scratch.GetCallersAsync("SimpleLibrary.Handler.AppendFormatted");
		string constructor = await Scratch.GetCallersAsync("SimpleLibrary.Handler.Handler");

		Assert.DoesNotContain("error=", appendLiteral);
		Assert.Contains("method,Interpolates,", appendLiteral);
		Assert.DoesNotContain("error=", appendFormatted);
		Assert.Contains("method,Interpolates,", appendFormatted);
		Assert.DoesNotContain("error=", constructor);
		Assert.Contains("method,Interpolates,", constructor);
	}

	[Fact]
	public async Task WhenAC14InstanceCompoundOperatorIsUsed_ThenTheEnclosingMethodIsReported()
	{
		string compound = await Scratch.GetCallersAsync("SimpleLibrary.Wallet.op_AdditionAssignment");
		string increment = await Scratch.GetCallersAsync("SimpleLibrary.Wallet.op_IncrementAssignment");

		Assert.DoesNotContain("error=", compound);
		Assert.Contains("method,InstanceCompound,", compound);
		Assert.DoesNotContain("error=", increment);
		Assert.Contains("method,InstanceCompound,", increment);
	}

	[Fact]
	public async Task WhenADeconstructionAssignmentIsUsed_ThenDeconstructsCallerIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Point2.Deconstruct");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Deconstructs,", result);
	}

	[Fact]
	public async Task WhenAPositionalPatternIsMatched_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.Point2.Deconstruct");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,PositionalPattern,", result);
	}

	[Fact]
	public async Task WhenACollectionInitializerAddsItems_ThenTheAddingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.BagList.Add");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,CollectionInitializer,", result);
	}

	[Fact]
	public async Task WhenADerivedConstructorHasNoInitializer_ThenTheBaseConstructorCallerIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.BaseThing.BaseThing");

		Assert.DoesNotContain("error=", result);
		// The caller is DerivedThing's own constructor, which the compiler gives an implicit base() call.
		Assert.Contains("class,DerivedThing\n", result);
		Assert.Contains("method,.ctor,", result);
	}

	[Fact]
	public async Task WhenAnIndexFromEndIsUsed_ThenTheIndexerAndItsLengthCallersAreReported()
	{
		string indexer = await Scratch.GetCallersAsync("SimpleLibrary.Rope.this[int]");
		string length = await Scratch.GetCallersAsync("SimpleLibrary.Rope.Length");

		Assert.DoesNotContain("error=", indexer);
		Assert.Contains("resolvedSymbol=SimpleLibrary.Rope.this[int]", indexer);
		Assert.Contains("method,FromEnd,", indexer);
		Assert.DoesNotContain("error=", length);
		Assert.Contains("method,FromEnd,", length);
	}

	[Fact]
	public async Task WhenAMethodGroupIsPassedAsADelegate_ThenTheEnclosingMethodIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.CoverageProbe.Visit");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,MethodGroup,", result);
	}

	[Fact]
	public async Task WhenTheReferenceIsInsideANameOf_ThenNoCallerIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.CallSites.Take");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,PassesMethodGroup,", result);
		Assert.DoesNotContain("method,NameOfOnly,", result);
	}

	[Fact]
	public async Task WhenAFieldInitializerCallsTheMethod_ThenTheFieldIsTheCaller()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.CoverageProbe.FieldInit");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("field,Field,", result);
	}

	[Fact]
	public async Task WhenAPropertyInitializerCallsTheMethod_ThenThePropertyIsTheCaller()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.CoverageProbe.PropertyInit");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("property,AutoWithInitializer,", result);
		Assert.DoesNotContain("k__BackingField", result);
	}

	[Fact]
	public async Task WhenTheCallSiteIsInsideAnAccessor_ThenTheCallerIsReportedAsTheProperty()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.CoverageProbe.AccessorGet");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("property,Accessors,", result);
		Assert.DoesNotContain("get_Accessors", result);
	}

	[Fact]
	public async Task WhenAnImplementationIsCalledThroughItsInterface_ThenTheCallerIsReported()
	{
		string result = await Scratch.GetCallersAsync("SimpleLibrary.RunnerImpl.Run");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ViaInterface,", result);
	}

	public sealed class ScratchSolution : IAsyncLifetime
	{
		private readonly InstanceRegistry Registry = new();
		private string SolutionPath = "";
		private SolutionModel Model = null!;
		private GetCallersTool Subject = null!;

		public async Task InitializeAsync()
		{
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			string project = Path.Combine(library, "SimpleLibrary.csproj");
			await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project))
				.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><LangVersion>14</LangVersion>"));
			await File.WriteAllTextAsync(Path.Combine(library, "CoverageProbe.cs"), CoverageFixture.Probe);
			await File.WriteAllTextAsync(Path.Combine(library, "CallerProbe.cs"), CoverageFixture.CallerProbe);

			RoslynInstance instance = await Registry.GetOrAddAsync(SolutionPath);
			Model = await instance.ReadModelAsync();
			Subject = new GetCallersTool(Registry, new SymbolResolver(), new ProjectionService());
		}

		public Task<string> GetCallersAsync(string memberName) => Subject.GetCallers(SolutionPath, memberName);

		public Solution? Solution => Model.Solution;

		public async Task<ISymbol?> ResolveAsync(string name)
		{
			IReadOnlyList<ISymbol> matches = await new SymbolResolver().FindByFullyQualifiedNameAsync(Model.Solution!, name);
			return matches.Count > 0 ? matches[0] : null;
		}

		/// <summary>The call kinds one member's operation tree reports, keyed by the callee's simple name.</summary>
		internal async Task<Dictionary<string, CallKind>> CalleeKindsAsync(string memberName)
		{
			ISymbol? member = await ResolveAsync(memberName);
			Assert.NotNull(member);

			SyntaxNode declaration = member!.DeclaringSyntaxReferences[0].GetSyntax();
			Document document = Model.Solution!.GetDocument(declaration.SyntaxTree)!;
			SemanticModel semanticModel = (await document.GetSemanticModelAsync())!;
			IOperation? operation = semanticModel.GetOperation(declaration);
			Assert.NotNull(operation);

			var kinds = new Dictionary<string, CallKind>();
			new CallCollector((callee, kind) => kinds[callee.Name] = kind).Visit(operation!);
			return kinds;
		}

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}

/// <summary>
/// The shape gate decides from the resolved target alone which implicit-call scans can pay off; ordinary
/// targets must pay nothing.
/// </summary>
public class ImplicitCallTargetGateTests : IClassFixture<GetCallersCoverageTests.ScratchSolution>
{
	private readonly GetCallersCoverageTests.ScratchSolution Scratch;

	public ImplicitCallTargetGateTests(GetCallersCoverageTests.ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenTheTargetIsAnOrdinaryMethod_ThenNoScanIsNeeded()
	{
		Assert.Equal(ImplicitCallScan.None, ImplicitCallTargets.ScansFor((await Scratch.ResolveAsync("SimpleLibrary.CoverageProbe.FieldInit"))!));
		Assert.Equal(ImplicitCallScan.None, ImplicitCallTargets.ScansFor((await Scratch.ResolveAsync("SimpleLibrary.BagList.Add"))!));
		Assert.Equal(ImplicitCallScan.None, ImplicitCallTargets.ScansFor((await Scratch.ResolveAsync("SimpleLibrary.RunnerImpl.Run"))!));
		Assert.Equal(ImplicitCallScan.None, ImplicitCallTargets.ScansFor((await Scratch.ResolveAsync("SimpleLibrary.Money.op_Addition"))!));
	}

	[Fact]
	public async Task WhenTheTargetIsAPlainDisposableDispose_ThenForEachIsNotScanned()
	{
		ISymbol? dispose = await Scratch.ResolveAsync("SimpleLibrary.Resource2.Dispose");

		Assert.NotNull(dispose);
		Assert.Equal(ImplicitCallScan.None, ImplicitCallTargets.ScansFor(dispose!));
	}

	[Fact]
	public async Task WhenTheTargetIsAnEnumeratorsDispose_ThenForEachIsScanned()
	{
		ISymbol? dispose = await Scratch.ResolveAsync("SimpleLibrary.BagEnumerator.Dispose");

		Assert.NotNull(dispose);
		Assert.Equal(ImplicitCallScan.ForEach, ImplicitCallTargets.ScansFor(dispose!));
	}

	[Fact]
	public async Task WhenTheTargetIsAConversionOperator_ThenConversionsAndForEachAreScanned()
	{
		ISymbol? opImplicit = await Scratch.ResolveAsync("SimpleLibrary.Money.op_Implicit");

		Assert.NotNull(opImplicit);
		Assert.Equal(ImplicitCallScan.Conversions | ImplicitCallScan.ForEach, ImplicitCallTargets.ScansFor(opImplicit!));
	}

	[Fact]
	public async Task WhenTheTargetIsAnAwaitersGetResultOrIsCompleted_ThenAwaitIsScanned()
	{
		ISymbol? getResult = await Scratch.ResolveAsync("SimpleLibrary.MyAwaiter.GetResult");
		ISymbol? isCompleted = await Scratch.ResolveAsync("SimpleLibrary.MyAwaiter.IsCompleted");

		Assert.NotNull(getResult);
		Assert.Equal(ImplicitCallScan.Await, ImplicitCallTargets.ScansFor(getResult!));
		Assert.NotNull(isCompleted);
		Assert.Equal(ImplicitCallScan.Await, ImplicitCallTargets.ScansFor(isCompleted!));
	}

	[Fact]
	public async Task WhenTheTargetBelongsToAnInterpolatedStringHandler_ThenInterpolatedStringsIsScanned()
	{
		ISymbol? appendLiteral = await Scratch.ResolveAsync("SimpleLibrary.Handler.AppendLiteral");
		ISymbol? constructor = await Scratch.ResolveAsync("SimpleLibrary.Handler.Handler");

		Assert.NotNull(appendLiteral);
		Assert.True(ImplicitCallTargets.ScansFor(appendLiteral!).HasFlag(ImplicitCallScan.InterpolatedStrings));
		Assert.NotNull(constructor);
		Assert.True(ImplicitCallTargets.ScansFor(constructor!).HasFlag(ImplicitCallScan.InterpolatedStrings));
	}

	[Fact]
	public async Task WhenTheTargetIsAQueryMethod_ThenQueriesIsScanned()
	{
		ISymbol? where = await Scratch.ResolveAsync("SimpleLibrary.QuerySource.Where");

		Assert.NotNull(where);
		Assert.Equal(ImplicitCallScan.Queries, ImplicitCallTargets.ScansFor(where!));
	}
}

/// <summary>
/// The shared collector tags the calls a member reports: a using's Dispose is ordinary, the foreach/await
/// plumbing the compiler inserts is tagged Plumbing (and filtered out of get_callees, which keeps listing
/// only the calls the source spells out).
/// </summary>
public class CallCollectorKindTests : IClassFixture<GetCallersCoverageTests.ScratchSolution>
{
	private readonly GetCallersCoverageTests.ScratchSolution Scratch;

	public CallCollectorKindTests(GetCallersCoverageTests.ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenATypeIsDisposedInAUsing_ThenTheDisposeIsOrdinary()
	{
		IReadOnlyDictionary<string, CallKind> kinds = await Scratch.CalleeKindsAsync("SimpleLibrary.CoverageProbe.Uses");

		Assert.Equal(CallKind.Ordinary, kinds["Dispose"]);
	}

	[Fact]
	public async Task WhenALoopUsesACustomEnumerator_ThenThePlumbingIsMarked()
	{
		IReadOnlyDictionary<string, CallKind> kinds = await Scratch.CalleeKindsAsync("SimpleLibrary.CallSites.ForEachBag");

		Assert.Equal(CallKind.Plumbing, kinds["GetEnumerator"]);
		Assert.Equal(CallKind.Plumbing, kinds["MoveNext"]);
		Assert.Equal(CallKind.Plumbing, kinds["get_Current"]);
		Assert.Equal(CallKind.Plumbing, kinds["Dispose"]);
	}

	[Fact]
	public async Task WhenAMethodAwaitsACustomAwaiter_ThenIsCompletedAndGetResultAreMarked()
	{
		IReadOnlyDictionary<string, CallKind> kinds = await Scratch.CalleeKindsAsync("SimpleLibrary.CallSites.AwaitCustom");

		Assert.Equal(CallKind.Plumbing, kinds["GetAwaiter"]);
		Assert.Equal(CallKind.Plumbing, kinds["get_IsCompleted"]);
		Assert.Equal(CallKind.Plumbing, kinds["GetResult"]);
	}
}

/// <summary>
/// A caller that only compiles in a branch inactive in the loaded configuration is still reported, through
/// the variant projection that toggles its condition: the base projection binds nothing there, the variant
/// re-scans the directive-containing tree.
/// </summary>
public class GetCallersProjectionTests : IClassFixture<GetCallersProjectionTests.ScratchSolution>
{
	private const string Source = """
		using System.Collections;
		using System.Collections.Generic;

		namespace ConditionalLib;

		public sealed class ColdBag : IEnumerable<int>
		{
			public ColdBagEnumerator GetEnumerator() => new();
			IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new NotSupportedException();
			IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
		}

		public struct ColdBagEnumerator : IEnumerator<int>
		{
			public int Current => 0;
			int IEnumerator<int>.Current => 0;
			public bool MoveNext() => false;
			public void Dispose() { }
			public void Reset() { }
		}

		#if !DEBUG
		public static class ColdSites
		{
			public static void Loop(ColdBag bag) { foreach (var item in bag) { } }
		}
		#endif
		""";

	private readonly ScratchSolution Scratch;

	public GetCallersProjectionTests(ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenTheCallerOnlyCompilesInAnInactiveBranch_ThenTheVariantProjectionReportsIt()
	{
		string result = await Scratch.GetCallersAsync("ConditionalLib.ColdBagEnumerator.Dispose");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Loop,", result);
	}

	public sealed class ScratchSolution : IAsyncLifetime
	{
		private readonly InstanceRegistry Registry = new();
		private string SolutionPath = "";
		private GetCallersTool Subject = null!;

		public async Task InitializeAsync()
		{
			SolutionPath = TestSolutions.CreateScratchConditionalSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "ConditionalLib");
			await File.WriteAllTextAsync(Path.Combine(library, "ImplicitProbe.cs"), Source);

			await Registry.GetOrAddAsync(SolutionPath);
			Subject = new GetCallersTool(Registry, new SymbolResolver(), new ProjectionService());
		}

		public Task<string> GetCallersAsync(string memberName) => Subject.GetCallers(SolutionPath, memberName);

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}
