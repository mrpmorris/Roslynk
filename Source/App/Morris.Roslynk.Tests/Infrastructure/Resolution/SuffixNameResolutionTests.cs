using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// Issue #118: a partially qualified name resolves when its dotted segments are a whole-segment suffix
/// of exactly one symbol's fully-qualified name. The suffix pass is the lowest-priority level, so exact
/// and relaxed matches keep winning, and a segment boundary is never crossed.
/// </summary>
public class SuffixNameResolutionTests : IClassFixture<SuffixNameResolutionTests.Fixture>
{
	private readonly Fixture Subject;

	public SuffixNameResolutionTests(Fixture fixture)
	{
		Subject = fixture;
	}

	[Fact]
	public async Task WhenAPartiallyQualifiedNameUniquelyMatchesBySuffix_ThenItResolves()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("SomeUIHelper.Grid_CustomizeElement");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("Beta.SomeUIHelper.Grid_CustomizeElement(int)", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenSeveralSymbolsShareTheSuffix_ThenEverySuffixMatchIsReturned()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("UIHelper.Grid_CustomizeElement");

		Assert.Equal(2, matches.Count);
		Assert.Equal(
			["Alpha.Alpha.UIHelper.Grid_CustomizeElement(int)", "Alpha.UIHelper.Grid_CustomizeElement(int)"],
			matches.Select(match => SymbolSignature.Of(match)).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task WhenASuffixWouldOnlyMatchAPartialSegment_ThenTheLongerNameIsNotMatched()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("UIHelper.Grid_CustomizeElement");

		Assert.DoesNotContain(matches, match => SymbolSignature.Of(match).Contains("SomeUIHelper", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenAnExactMatchExists_ThenSuffixMatchesAreNotAdded()
	{
		// Alpha.Alpha.UIHelper ends with the written segments, but Alpha.UIHelper is the exact name.
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Alpha.UIHelper.Grid_CustomizeElement");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("Alpha.UIHelper.Grid_CustomizeElement(int)", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenAGenericArityIsWritten_ThenOnlyThatArityMatchesBySuffix()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("UIHelper.Box<T>");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("Gamma.UIHelper.Box<T>", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenAParameterListIsWritten_ThenSuffixMatchingStillConstrainsIt()
	{
		Assert.Empty(await Subject.ResolveAsync("UIHelper.Grid_CustomizeElement(string)"));
		Assert.Equal(2, (await Subject.ResolveAsync("UIHelper.Grid_CustomizeElement(int)")).Count);
	}

	[Fact]
	public async Task WhenABareNameIsGiven_ThenSuffixMatchingDoesNotChangeItsSimpleNameBehaviour()
	{
		// Unqualified names match on the simple name and keep reporting every same-named symbol.
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Grid_CustomizeElement");

		Assert.Equal(3, matches.Count);
	}

	[Fact]
	public async Task WhenAnAmbiguousCandidateIsSentBackVerbatim_ThenItResolvesToOneSymbol()
	{
		IReadOnlyList<ISymbol> ambiguous = await Subject.ResolveAsync("UIHelper.Grid_CustomizeElement");
		IReadOnlyList<string> candidates = SymbolSignature.Distinguish(ambiguous);

		Assert.Equal(2, candidates.Count);
		foreach (string candidate in candidates)
		{
			ISymbol resolved = Assert.Single(await Subject.ResolveAsync(candidate));
			Assert.Equal(candidate, SymbolSignature.Of(resolved));
		}
	}

	public sealed class Fixture : IAsyncLifetime
	{
		private InstanceRegistry Registry = null!;

		public string SolutionPath { get; private set; } = null!;

		public RoslynInstance Instance { get; private set; } = null!;

		private const string Probe = """
			namespace Alpha
			{
				public class UIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}

			namespace Alpha.Alpha
			{
				public class UIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}

			namespace Beta
			{
				public class SomeUIHelper
				{
					public static int Grid_CustomizeElement(int value) => value;
				}
			}

			namespace Gamma.UIHelper
			{
				public class Box<T>
				{
					public T? Value;
				}
			}

			namespace Delta.UIHelper
			{
				public class Box<T, U>
				{
				}
			}
			""";

		public async Task InitializeAsync()
		{
			Registry = new InstanceRegistry();
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			await File.WriteAllTextAsync(Path.Combine(library, "SuffixProbe.cs"), Probe);
			Instance = await Registry.GetOrAddAsync(SolutionPath);
		}

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<ISymbol>> ResolveAsync(string name) =>
			new SymbolResolver().FindByFullyQualifiedNameAsync(Instance.CurrentSolution, name);
	}
}
