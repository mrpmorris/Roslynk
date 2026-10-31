using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Features.Symbols.SearchSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols;

/// <summary>
/// Issue #176 acceptance through the read tools: a positional record's property is a real, non-implicit
/// source declaration with a syntax reference at the parameter list, but the declaration search that
/// <see cref="SymbolResolver"/> uses never indexes it. get_members and search_symbols already find it by
/// other means, so the tools disagree; get_symbol and get_symbol_body must agree with them.
/// </summary>
public class PositionalRecordPropertyResolutionTests : IClassFixture<PositionalRecordPropertyResolutionTests.Fixture>
{
	private readonly Fixture Subject;

	public PositionalRecordPropertyResolutionTests(Fixture fixture)
	{
		Subject = fixture;
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedFully_ThenGetSymbolReturnsIt()
	{
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch.Targets");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("path=SimpleLibrary/PositionalRecord.cs", result);
		// The declaration site is the record's parameter list, so the declarator is the parameter.
		Assert.Contains("Dictionary<string, int> Targets", result);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedBySuffix_ThenGetSymbolEchoesResolvedSymbol()
	{
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "OrderPage.Batch.Targets");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=Acme.Web.Pages.Orders.OrderPage.Batch.Targets\n", result);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsRequested_ThenGetSymbolBodyReturnsItsDeclarator()
	{
		string result = await Subject.Bodies.GetSymbolBody(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch.Targets");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedSymbol=Acme.Web.Pages.Orders.OrderPage.Batch.Targets\n", result);
		Assert.Contains("Dictionary<string, int> Targets", result);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsRequested_ThenGetMembersAndSearchSymbolsAgreeWithGetSymbol()
	{
		// get_members and search_symbols already see the property; the resolver-based tools must not
		// contradict them.
		string members = await Subject.Members.GetMembers(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch");
		Assert.Contains("property,Targets,", members);

		string search = await Subject.Searcher.SearchSymbols(Subject.SolutionPath, "Targets");
		Assert.Contains("property,Targets,", search);

		string symbol = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch.Targets");
		Assert.DoesNotContain("error=", symbol);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamed_ThenTheOrdinarySameNamedPropertyStillResolves()
	{
		// The fixture declares Holder.Targets as well; fixing the record property must not shadow it.
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Holder.Targets");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("path=SimpleLibrary/PositionalRecord.cs", result);
		Assert.Contains("private int Targets { get; set; }", result);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamed_ThenTheRecordTypeItselfIsUnaffected()
	{
		// A type without a body (a semicolon-terminated positional record) makes get_symbol throw while
		// slicing its declaration header. That is a separate defect from #176; asserting it here would
		// pin broken behaviour, so this test covers a record that HAS a body, which is the issue's shape.
		string result = await Subject.Symbols.GetSymbol(Subject.SolutionPath, "Acme.Web.Pages.Orders.OrderPage.Holder");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("private sealed class Holder", result);
	}

	public sealed class Fixture : IAsyncLifetime
	{
		public InstanceRegistry Registry { get; private set; } = null!;

		public string SolutionPath { get; private set; } = null!;

		public GetSymbolTool Symbols { get; private set; } = null!;

		public GetSymbolBodyTool Bodies { get; private set; } = null!;

		public GetMembersTool Members { get; private set; } = null!;

		public SearchSymbolsTool Searcher { get; private set; } = null!;

		private const string Probe = """
			namespace Acme.Web.Pages.Orders;

			public partial class OrderPage
			{
				private sealed record Batch(List<string> Items, Dictionary<string, int> Targets);

				private sealed class Holder
				{
					private int Targets { get; set; }
				}
			}
			""";

		public async Task InitializeAsync()
		{
			Registry = new InstanceRegistry();
			SolutionPath = TestSolutions.CreateScratchSimpleSolution();
			string library = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
			await File.WriteAllTextAsync(Path.Combine(library, "PositionalRecord.cs"), Probe);
			await Registry.GetOrAddAsync(SolutionPath);

			var resolver = new SymbolResolver();
			var projections = new ProjectionService();
			Symbols = new GetSymbolTool(Registry, resolver, projections);
			Bodies = new GetSymbolBodyTool(Registry, resolver, projections);
			Members = new GetMembersTool(Registry, resolver, projections);
			Searcher = new SearchSymbolsTool(Registry, projections);
		}

		public Task DisposeAsync()
		{
			Registry.Dispose();
			return Task.CompletedTask;
		}
	}
}
