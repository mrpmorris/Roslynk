using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// Issue #176 at the resolver seam: a positional record's property is a real, non-implicit source
/// declaration whose <c>DeclaringSyntaxReferences</c> point at the record's parameter list. Roslyn's
/// <c>SymbolFinder.FindDeclarationsAsync</c> indexes member declarations only, so it never reports the
/// property; <see cref="SymbolSignature.Classify"/> would match it exactly if it were ever offered.
/// These tests pin both halves: the symbol is matchable, and the resolver must find it.
/// </summary>
public class PositionalRecordPropertyResolutionTests : IClassFixture<PositionalRecordPropertyResolutionTests.Fixture>
{
	private readonly Fixture Subject;

	public PositionalRecordPropertyResolutionTests(Fixture fixture)
	{
		Subject = fixture;
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedFully_ThenTheResolverFindsIt()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Acme.Web.Pages.Orders.OrderPage.Batch.Targets");

		ISymbol match = Assert.Single(matches);
		Assert.Equal(SymbolKind.Property, match.Kind);
		Assert.Equal("Acme.Web.Pages.Orders.OrderPage.Batch.Targets", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedBySuffix_ThenTheResolverFindsIt()
	{
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("OrderPage.Batch.Targets");

		ISymbol match = Assert.Single(matches);
		Assert.Equal("Acme.Web.Pages.Orders.OrderPage.Batch.Targets", SymbolSignature.Of(match));
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedByItsBareName_ThenEverySameNamedPropertyIsReturned()
	{
		// A bare name matches on the simple name, so the positional property and the ordinary property of
		// the same name are both reported — the caller disambiguates by qualified name.
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Targets");

		Assert.Equal(
			["Acme.Web.Pages.Orders.OrderPage.Batch.Targets", "Acme.Web.Pages.Orders.OrderPage.Holder.Targets"],
			matches.Select(match => SymbolSignature.Of(match)).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsSuggested_ThenTheSuggestionResolvesBackToItself()
	{
		// The reported bug's second call: the suggested candidate is a name the resolver then rejects.
		IReadOnlyList<string> suggestions = await Subject.SuggestAsync("OrderPage.Batch.Target");

		Assert.Contains("Acme.Web.Pages.Orders.OrderPage.Batch.Targets", suggestions);

		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Acme.Web.Pages.Orders.OrderPage.Batch.Targets");
		Assert.Single(matches);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsInspected_ThenItIsNotAnImplicitDeclaration()
	{
		// The property is declared by source (its syntax reference is the parameter), which is why it is
		// renamable at all. Only the record's compiler-synthesized members are implicit.
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Acme.Web.Pages.Orders.OrderPage.Batch.Targets");

		ISymbol match = Assert.Single(matches);
		Assert.False(match.IsImplicitlyDeclared);
		Assert.Single(match.DeclaringSyntaxReferences);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsClassified_ThenItMatchesExactly()
	{
		// Confirms the symbol is not merely found but matches: resolution is only broken at lookup.
		IReadOnlyList<ISymbol> matches = await Subject.ResolveAsync("Acme.Web.Pages.Orders.OrderPage.Batch.Targets");

		ISymbol match = Assert.Single(matches);
		Assert.True(SymbolSignature.TryParse("Acme.Web.Pages.Orders.OrderPage.Batch.Targets", out SymbolSignatureQuery query));
		Assert.Equal(SymbolSignature.SymbolMatchKind.Exact, SymbolSignature.Classify(match, query));
	}

	public sealed class Fixture : IAsyncLifetime
	{
		public string SolutionPath { get; private set; } = null!;

		public RoslynInstance Instance { get; private set; } = null!;

		public async Task InitializeAsync()
		{
			SolutionPath = TestSolutions.CreateScratchSimpleSolutionWithPositionalRecord();
			Instance = await TestSolutions.SharedInstanceAsync(SolutionPath);
		}

		public Task DisposeAsync() => Task.CompletedTask;

		public Task<IReadOnlyList<ISymbol>> ResolveAsync(string name) =>
			new SymbolResolver().FindByFullyQualifiedNameAsync(Instance.CurrentSolution, name);

		public Task<IReadOnlyList<string>> SuggestAsync(string name) =>
			new SymbolResolver().SuggestAsync(Instance.CurrentSolution, name);
	}
}
