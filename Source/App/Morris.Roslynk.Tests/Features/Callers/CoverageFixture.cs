namespace Morris.Roslynk.Tests.Features.Callers;

/// <summary>
/// The one probe source shared by the get_callees and get_callers coverage suites, so the two views of "a
/// call" are tested against the same shapes and cannot drift. <see cref="Probe"/> is written into a scratch
/// copy of SimpleSolution as CoverageProbe.cs by both suites; <see cref="CallerProbe"/> is the caller-side
/// addition (custom enumerator/awaiter/handler shapes) that only GetCallersCoverageTests writes, because it
/// needs LangVersion 14 for the C# 14 instance operators.
/// </summary>
internal static class CoverageFixture
{
	public const string Probe = """
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
			public static explicit operator string(Money value) => value.Value.ToString();
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

	public const string CallerProbe = """
		using System;
		using System.Collections;
		using System.Collections.Generic;
		using System.Runtime.CompilerServices;
		using System.Threading.Tasks;

		namespace SimpleLibrary;

		// The shapes whose compiler-inserted calls get_callers must report. Every target is declared in the
		// solution: the resolver only ever reaches source declarations.

		public sealed class Bag : IEnumerable<int>
		{
			public BagEnumerator GetEnumerator() => new();
			IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new NotSupportedException();
			IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
		}

		public struct BagEnumerator : IEnumerator<int>
		{
			public int Current => 0;
			int IEnumerator<int>.Current => Current;
			object IEnumerator.Current => Current;
			public bool MoveNext() => false;
			public void Dispose() { }
			public void Reset() { }
		}

		public sealed class AsyncBag
		{
			public AsyncBagEnumerator GetAsyncEnumerator() => new();
		}

		public struct AsyncBagEnumerator
		{
			public int Current => 0;
			public MyBoolAwaitable MoveNextAsync() => new();
			public ValueTask DisposeAsync() => default;
		}

		public sealed class MyBoolAwaiter : INotifyCompletion
		{
			public bool IsCompleted => true;
			public bool GetResult() => true;
			public void OnCompleted(Action continuation) { }
		}

		public sealed class MyBoolAwaitable
		{
			public MyBoolAwaiter GetAwaiter() => new();
		}

		public sealed class MyAwaiter : INotifyCompletion
		{
			public bool IsCompleted => true;
			public void GetResult() { }
			public void OnCompleted(Action continuation) { }
		}

		public sealed class MyAwaitable
		{
			public MyAwaiter GetAwaiter() => new();
		}

		[InterpolatedStringHandler]
		public sealed class Handler
		{
			public Handler(int literalLength, int formattedCount) { }
			public void AppendLiteral(string value) { }
			public void AppendFormatted<T>(T value) { }
		}

		public static class Logger
		{
			public static void Log(Handler handler) { }
		}

		public sealed class QuerySource
		{
			public QuerySource Where(Func<int, bool> predicate) => this;
			public QuerySource Select(Func<int, int> selector) => this;
		}

		public sealed class Point2
		{
			public void Deconstruct(out int x, out int y) { x = 0; y = 0; }
		}

		public sealed class Rope
		{
			public int Length => 0;
			public int this[int index] => 0;
		}

		public class BaseThing
		{
			public BaseThing() { BaseCtorBody(); }
			public void BaseCtorBody() { }
		}

		public sealed class DerivedThing : BaseThing
		{
			public DerivedThing() { }
		}

		public class DerivedRecord(int Amount) : BaseThing;

		public interface IRunner
		{
			void Run();
		}

		public sealed class RunnerImpl : IRunner
		{
			public void Run() { }
		}

		public sealed class Resource2 : IDisposable
		{
			public void Dispose() { }
		}

		public sealed class AsyncResource2 : IAsyncDisposable
		{
			public ValueTask DisposeAsync() => default;
		}

		public struct Flag
		{
			public static bool operator true(Flag flag) => true;
			public static bool operator false(Flag flag) => false;
		}

		public struct Wallet
		{
			public int Amount;
			public void operator +=(Wallet other) { Amount += other.Amount; }
			public void operator ++() { Amount++; }
		}

		public sealed class BagList : IEnumerable<int>
		{
			public BagList() { }
			public void Add(int item) { }
			IEnumerator<int> IEnumerable<int>.GetEnumerator() => throw new NotSupportedException();
			IEnumerator IEnumerable.GetEnumerator() => throw new NotSupportedException();
		}

		public static class CallSites
		{
			public static void Compound(Money money) { money += money; }
			public static void ExplicitCast(Money money) { string text = (string)money; }
			public static void InstanceCompound(Wallet wallet, Wallet other) { wallet += other; wallet++; }
			public static void ImplicitConvert(Money money) { TakeInt(money); }
			public static void ForEachMoney(MoneyBag bag) { foreach (int number in bag) { } }
			public static void ForEachBag(Bag bag) { foreach (var item in bag) { } }
			public static async Task AwaitForeachBag(AsyncBag bag) { await foreach (var item in bag) { } }
			public static async Task AwaitCustom(MyAwaitable awaitable) { await awaitable; }
			public static async Task AwaitUsingAsync() { await using var resource = new AsyncResource2(); }
			public static void DeclaresDisposable() { Resource2 resource = new Resource2(); }
			public static void AlsoReallyDisposes() { Resource2 keep = new Resource2(); using var used = new Resource2(); }
			public static void UsesResource() { using var used = new Resource2(); }
			public static void Condition(Flag flag) { if (flag) { } }
			public static void Queries(QuerySource source) { var query = from x in source where x > 0 select x * 2; }
			public static void Interpolates(int value) { Logger.Log($"a{value}b"); }
			public static void PositionalPattern(Point2 point) { if (point is (1, 2)) { } }
			public static void FromEnd(Rope rope) { int x = rope[^1]; }
			public static void Deconstructs(Point2 point) { var (x, y) = point; }
			public static void NewDerived() { BaseThing thing = new DerivedThing(); }
			public static void ViaInterface(IRunner runner) { runner.Run(); }
			public static void PassesMethodGroup(List<int> numbers) { numbers.ForEach(Take); }
			public static void NameOfOnly() { string name = nameof(Take); }
			public static void CollectionInitializer() { BagList list = new BagList { 1, 2 }; }
			public static void PlainInt() { int x = 1; _ = x; }

			private static void Take(int value) { }
			private static void TakeInt(int value) { }
		}

		public sealed class MoneyBag
		{
			public MoneyBagEnumerator GetEnumerator() => new();
		}

		public struct MoneyBagEnumerator
		{
			public Money Current => default;
			public bool MoveNext() => false;
			public void Dispose() { }
		}
		""";
}
