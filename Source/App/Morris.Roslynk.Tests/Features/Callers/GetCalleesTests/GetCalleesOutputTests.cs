using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCalleesTests;

/// <summary>
/// The shape of get_callees' leaves: external callees under one '&lt;external:Assembly&gt;' bucket per assembly
/// with an empty loc field, a parameter-types field only when the callee's own type overloads the name ('()' when
/// the overload is parameterless), and every callee reported as declared (generic definitions, extension methods
/// with their 'this' parameter). Runs against a scratch copy of SimpleSolution with an extra source file.
/// </summary>
public class GetCalleesOutputTests : IClassFixture<GetCalleesOutputTests.ScratchSolution>
{
	private const string Member = "SimpleLibrary.Output.Report.Save";

	private const string Source = """
		using System;
		using System.Collections.Generic;
		using System.IO;
		using System.Linq;

		namespace SimpleLibrary.Output;

		public static class Audit
		{
			public static void Log(int id) { }
		}

		public static class IntExtensions
		{
			public static int Twice(this int x) => x * 2;
			public static int Twice(this int x, int times) => x * 2 * times;
		}

		public static class Conv
		{
			public static T To<T>(string value) => default!;
			public static T To<T>(string value, T fallback) => fallback;
		}

		public class Box<T>
		{
			public void Put(T item) { }
		}

		public class Tally
		{
			public void Add(int amount) { }
			public void Add(int amount, int times) { }
			public void Reset() { }
			public void Reset(int to) { }
		}

		public class Base
		{
			public void Store(int value) { }
		}

		public class Derived : Base
		{
			public void Store(string value) { }
		}

		public class Report
		{
			public void Save(string path, int id, List<int> numbers, Tally tally)
			{
				string json = string.Join(",", id);
				File.WriteAllText(path, json);
				Console.WriteLine(json.Length);
				Console.WriteLine(id.ToString());
				var doubled = numbers.Select(number => number.Twice());
				int a = Conv.To<int>("1");
				string b = Conv.To<string>("x");
				new Box<int>().Put(1);
				new Box<string>().Put("s");
				tally.Add(id);
				tally.Reset();
				new Derived().Store("s");
				int.TryParse(json, out int parsed);
				Audit.Log(id);
			}
		}
		""";

	private readonly ScratchSolution Scratch;

	public GetCalleesOutputTests(ScratchSolution scratch) => Scratch = scratch;

	[Fact]
	public async Task WhenCalleesAreExternal_ThenEachAssemblyHasItsOwnBucket()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("\n<external:System.Console>\n", result);
		Assert.Contains("\n<external:System.Runtime>\n", result);
		Assert.Contains("\n<external:System.Linq>\n", result);
		Assert.DoesNotContain("<metadata", result);
	}

	[Fact]
	public async Task WhenAnExternalCalleeIsNotOverloaded_ThenItsLocFieldIsEmptyAndThereAreNoParameterTypes()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Contains("\tmethod,get_Length,\n", result);
	}

	[Fact]
	public async Task WhenAnExternalCalleeIsOverloaded_ThenEachOverloadIsADistinctLeafWithItsParameterTypes()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Contains("\tmethod,WriteLine,,int\n", result);
		// Nullable annotations are kept, as get_members renders them.
		Assert.Contains("\tmethod,WriteLine,,string?\n", result);
		Assert.Contains("\tmethod,WriteAllText,,string|string?\n", result);
	}

	[Fact]
	public async Task WhenAParameterlessOverloadIsCalled_ThenItsParameterTypesAreEmptyParentheses()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Contains("\tmethod,ToString,,()\n", result);
		Assert.Matches(@"\tmethod,Reset,\d+:\d+,\(\)\n", result);
	}

	[Fact]
	public async Task WhenASourceCalleeIsOverloaded_ThenItCarriesItsLocAndParameterTypes()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Matches(@"\tmethod,Add,\d+:\d+,int\n", result);
		// Add(int, int) is an overload that is not called.
		Assert.DoesNotContain("int|int", result);
	}

	[Fact]
	public async Task WhenASourceCalleeIsNotOverloaded_ThenItIsKindNameAndLocOnly()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Matches(@"\tmethod,Log,\d+:\d+\n", result);
	}

	[Fact]
	public async Task WhenAnOverloadIsOnlyInherited_ThenTheNameDoesNotCountAsOverloaded()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		// Derived declares one Store; Base.Store(int) does not make it overloaded.
		Assert.Matches(@"\tmethod,Store,\d+:\d+\n", result);
		Assert.DoesNotMatch(@"method,Store,\d+:\d+,", result);
	}

	[Fact]
	public async Task WhenGenericInstantiationsAreCalled_ThenTheyCollapseToTheDeclaredDefinition()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		// To<int> and To<string> are one declared To<T>(string); Box<int>.Put and Box<string>.Put one Put.
		Assert.Single(Regex.Matches(result, @"method,To,"));
		Assert.Matches(@"\tmethod,To,\d+:\d+,string\n", result);
		Assert.Matches(@"class,Box\n\t+method,\.ctor,\d+:\d+\n\t+method,Put,\d+:\d+\n", result);
		string body = result[result.IndexOf("\n\n", StringComparison.Ordinal)..];
		Assert.DoesNotContain("<int>", body);
		Assert.DoesNotContain("<string>", body);
	}

	[Fact]
	public async Task WhenAnExtensionMethodIsCalled_ThenItIsReportedInItsDeclaredFormWithTheThisParameter()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Matches(@"\tmethod,Twice,\d+:\d+,int\n", result);
		Assert.Contains("\tmethod,Select,,IEnumerable<TSource>|'Func<TSource, TResult>'\n", result);
	}

	[Fact]
	public async Task WhenAnOverloadTakesAnOutParameter_ThenTheParameterTypesCarryNoRefKind()
	{
		string result = await Scratch.GetCalleesAsync(Member);

		Assert.Contains("\tmethod,TryParse,,string?|int\n", result);
	}

	[Fact]
	public async Task WhenExternalCalleesAreExcluded_ThenOnlyTheSolutionsOwnMembersAreListed()
	{
		string result = await Scratch.GetCalleesAsync(Member, excludeExternal: true);

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("<external", result);
		Assert.Matches(@"\tmethod,Log,\d+:\d+\n", result);
		Assert.Matches(@"\tmethod,Twice,\d+:\d+,int\n", result);
	}

	[Fact]
	public async Task WhenExcludeExternalIsPassedThroughMultiQuery_ThenItIsBoundAndApplied()
	{
		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(Scratch.Registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.AddSingleton<ConditionalCoverage>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, Scratch.Registry);

		string envelope = await subject.MultiQuery(
			Scratch.SolutionPath,
			[
				new(MultiQueryOp.get_callees, new Dictionary<string, JsonElement>
				{
					["memberName"] = JsonSerializer.SerializeToElement(Member),
					["excludeExternal"] = JsonSerializer.SerializeToElement(true),
				}),
			]);

		Assert.DoesNotContain("error=", envelope);
		Assert.DoesNotContain("<external", envelope);
		Assert.Contains("method,Log,", envelope);
	}

	public sealed class ScratchSolution : IAsyncLifetime
	{
		public InstanceRegistry Registry { get; } = new();
		public string SolutionPath { get; private set; } = "";
		private GetCalleesTool Subject = null!;

		public async Task InitializeAsync()
		{
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			await File.WriteAllTextAsync(Path.Combine(library, "Output.cs"), Source);

			await Registry.GetOrAddAsync(SolutionPath);
			Subject = new GetCalleesTool(Registry, new SymbolResolver(), new ProjectionService());
		}

		public Task<string> GetCalleesAsync(string memberName, bool excludeExternal = false) =>
			Subject.GetCallees(SolutionPath, memberName, excludeExternal);

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}
