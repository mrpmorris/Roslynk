using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCalleesTests;

/// <summary>
/// What counts as a member's code and as a call: property/field/constructor bodies and initializers, user-defined
/// operators, using disposal, event accessors and method groups, but not the foreach/await plumbing. Runs against a
/// scratch copy of SimpleSolution with an extra source file, so the committed fixture stays untouched.
/// </summary>
public class GetCalleesCoverageTests : IClassFixture<GetCalleesCoverageTests.ScratchSolution>
{
	private const string Source = """
		using System;
		using System.Collections.Generic;
		using System.Threading.Tasks;

		namespace SimpleLibrary;

		public struct Money
		{
			public int Value;
			public static Money operator +(Money left, Money right) => left;
			public static Money operator ++(Money value) => value;
			public static implicit operator int(Money value) => value.Value;
		}

		public class Resource : IDisposable
		{
			public void Dispose() { }
		}

		public class CoverageProbe
		{
			public event EventHandler? Changed;
			public int Prop { get; set; }
			public static int StaticField = StaticInit();
			public int Field = FieldInit();
			public int Computed => ExpressionBodied();
			public int Accessors { get => AccessorGet(); set => AccessorSet(value); }
			public int AutoWithInitializer { get; set; } = PropertyInit();

			public CoverageProbe() { DefaultBody(); }
			public CoverageProbe(int chained) : this() { ChainedBody(); }

			public void UserOperators(Money money)
			{
				money += money;
				money++;
				int number = money;
			}

			public void ForEach(List<int> numbers) { foreach (int number in numbers) { } }
			public async Task Awaits() { await Task.Delay(1); }
			public void ExplicitAwaiter(Task task) { task.GetAwaiter().GetResult(); }
			public void Uses() { using var resource = new Resource(); }
			public void Subscribes() { Changed += Handler; }
			public void MethodGroup(List<int> numbers) { numbers.ForEach(Visit); }
			public void Increments() { Prop++; }
			public void ReadsEventField() { Changed?.Invoke(this, EventArgs.Empty); }

			private void Handler(object? sender, EventArgs e) { }
			private void Visit(int number) { }
			private static int StaticInit() => 0;
			private static int FieldInit() => 0;
			private static int PropertyInit() => 0;
			private static int ExpressionBodied() => 0;
			private static int AccessorGet() => 0;
			private static void AccessorSet(int value) { }
			private static void DefaultBody() { }
			private static void ChainedBody() { }
		}
		""";

	private readonly ScratchSolution Scratch;

	public GetCalleesCoverageTests(ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenTheMemberIsAnExpressionBodiedProperty_ThenItsExpressionCallsAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Computed");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ExpressionBodied,", result);
	}

	[Fact]
	public async Task WhenTheMemberIsAPropertyWithAccessorBodies_ThenBothAccessorsCallsAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Accessors");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,AccessorGet,", result);
		Assert.Contains("method,AccessorSet,", result);
	}

	[Fact]
	public async Task WhenTheMemberIsAnAutoPropertyWithAnInitializer_ThenTheInitializerCallIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.AutoWithInitializer");

		Assert.Contains("method,PropertyInit,", result);
	}

	[Fact]
	public async Task WhenTheMemberIsAnAutoPropertyWithoutAnInitializer_ThenNothingIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Prop");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,", result);
	}

	[Fact]
	public async Task WhenTheMemberIsAFieldWithAnInitializer_ThenTheInitializerCallIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.StaticField");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,StaticInit,", result);
	}

	[Fact]
	public async Task WhenAConstructorHasInitializers_ThenTheirCallsAreReportedWithTheBody()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.CoverageProbe()");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,DefaultBody,", result);
		Assert.Contains("method,FieldInit,", result);
		Assert.Contains("method,PropertyInit,", result);
		// Static initializers belong to the static constructor, not to an instance constructor.
		Assert.DoesNotContain("method,StaticInit,", result);
	}

	[Fact]
	public async Task WhenAConstructorChainsToThis_ThenTheInitializersAreLeftToTheChainedConstructor()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.CoverageProbe(int)");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,ChainedBody,", result);
		Assert.Contains("method,.ctor,", result);
		Assert.DoesNotContain("method,FieldInit,", result);
		Assert.DoesNotContain("method,DefaultBody,", result);
	}

	[Fact]
	public async Task WhenOperatorsAndConversionsAreUserDefined_ThenTheirMethodsAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.UserOperators");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,op_Addition,", result);   // money += money
		Assert.Contains("method,op_Increment,", result);  // money++
		Assert.Contains("method,op_Implicit,", result);   // int number = money
	}

	[Fact]
	public async Task WhenAMethodUsesForEach_ThenOnlyTheCallsInTheSourceAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.ForEach");

		Assert.DoesNotContain("error=", result);
		// The enumeration plumbing the compiler inserts is not written in the source.
		Assert.DoesNotContain("GetEnumerator", result);
		Assert.DoesNotContain("MoveNext", result);
		Assert.DoesNotContain("get_Current", result);
	}

	[Fact]
	public async Task WhenAMethodAwaits_ThenTheAwaitedCallIsReportedWithoutTheAwaiterPlumbing()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Awaits");

		Assert.DoesNotContain("error=", result);
		// Task.Delay is external (empty loc) and overloaded, so its parameter types follow.
		Assert.Contains("method,Delay,,int\n", result);
		Assert.DoesNotContain("GetAwaiter", result);
		Assert.DoesNotContain("get_IsCompleted", result);
		Assert.DoesNotContain("GetResult", result);
	}

	[Fact]
	public async Task WhenGetAwaiterIsCalledExplicitly_ThenItIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.ExplicitAwaiter");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,GetAwaiter,\n", result);
		Assert.Contains("method,GetResult,\n", result);
	}

	[Fact]
	public async Task WhenAMethodUsesAUsingDeclaration_ThenDisposeIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Uses");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("class,Resource\n", result);
		Assert.Contains("method,Dispose,", result);
		Assert.Contains("method,.ctor,", result);
	}

	[Fact]
	public async Task WhenAnEventIsSubscribed_ThenTheAddAccessorAndTheMethodGroupAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Subscribes");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,add_Changed,", result);
		Assert.Contains("method,Handler,", result);
		Assert.DoesNotContain("method,remove_Changed,", result);
	}

	[Fact]
	public async Task WhenAMethodGroupIsPassedAsADelegate_ThenTheMethodAndTheConsumingCallAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.MethodGroup");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Visit,", result);
		Assert.Contains("method,ForEach,\n", result);
	}

	[Fact]
	public async Task WhenAPropertyIsIncremented_ThenBothAccessorsAreReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Increments");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,get_Prop,", result);
		Assert.Contains("method,set_Prop,", result);
	}

	[Fact]
	public async Task WhenAFieldLikeEventIsOnlyInvoked_ThenNoAccessorIsReported()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.ReadsEventField");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Invoke,\n", result);
		Assert.DoesNotContain("add_Changed", result);
		Assert.DoesNotContain("remove_Changed", result);
	}

	[Fact]
	public async Task WhenCalleesAreFromTheFrameworkLibrary_ThenTheyAreGroupedUnderTheirAssembly()
	{
		string result = await Scratch.GetCalleesAsync("SimpleLibrary.CoverageProbe.Awaits");

		Assert.Contains("<external:System.Runtime>\n", result);
		Assert.Contains("class,Task\n", result);
	}

	public sealed class ScratchSolution : IAsyncLifetime
	{
		private readonly InstanceRegistry Registry = new();
		private string SolutionPath = "";
		private GetCalleesTool Subject = null!;

		public async Task InitializeAsync()
		{
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			await File.WriteAllTextAsync(Path.Combine(library, "CoverageProbe.cs"), Source);

			await Registry.GetOrAddAsync(SolutionPath);
			Subject = new GetCalleesTool(Registry, new SymbolResolver(), new ProjectionService());
		}

		public Task<string> GetCalleesAsync(string memberName) => Subject.GetCallees(SolutionPath, memberName);

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}
