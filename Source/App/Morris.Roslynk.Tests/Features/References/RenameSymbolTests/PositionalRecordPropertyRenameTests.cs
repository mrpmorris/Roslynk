using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

/// <summary>
/// Issue #176: a positional record's property is declared by the record's parameter list, and Roslyn's
/// declaration search (<c>SymbolFinder.FindDeclarationsAsync</c>) indexes only member declarations, so it
/// never reports the property — not even for a fully-qualified name it lists itself as a suggestion. The
/// fixture holds BOTH a positional-record <c>Targets</c> and an ordinary <c>Targets</c> property, so a fix
/// that merely widens matching cannot pass: the record property must resolve, and the unrelated property
/// with the same name must stay untouched.
/// </summary>
public class PositionalRecordPropertyRenameTests
{
	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedBySuffix_ThenItResolvesAndIsRenamedEverywhere()
	{
		string solutionPath = CreateScratchWithPositionalRecord();
		string recordPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "PositionalRecord.cs");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "OrderPage.Batch.Targets", "UriByIdLookup");

		Assert.Contains("applied=Y", result);

		string record = await File.ReadAllTextAsync(recordPath);
		// The property is declared by the parameter list, so the parameter itself is the declaration site.
		Assert.Contains("private sealed record Batch(List<string> Items, Dictionary<string, int> UriByIdLookup);", record);
		// Only the record's property was targeted: the unrelated Holder.Targets declaration survives verbatim.
		Assert.Contains("private int Targets { get; set; }", record);
		// A positional deconstruction binds by position, so it is unaffected by the property's name.
		Assert.Contains("var (items, targets) = Build();", record);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNamedFully_ThenItResolvesToTheSameProperty()
	{
		string solutionPath = CreateScratchWithPositionalRecord();
		string recordPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "PositionalRecord.cs");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch.Targets", "UriByIdLookup");

		Assert.Contains("applied=Y", result);
		// The request was already fully qualified, so the canonical name is echoed too.
		Assert.Contains("resolvedSymbol=Acme.Web.Pages.Orders.OrderPage.Batch.Targets\n", result);

		string record = await File.ReadAllTextAsync(recordPath);
		Assert.Contains("Dictionary<string, int> UriByIdLookup", record);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsRenamed_ThenTheUnrelatedSameNamedPropertyIsUntouched()
	{
		string solutionPath = CreateScratchWithPositionalRecord();
		string recordPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "PositionalRecord.cs");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "OrderPage.Batch.Targets", "UriByIdLookup");

		Assert.Contains("applied=Y", result);

		string record = await File.ReadAllTextAsync(recordPath);
		// OrderPage.Holder.Targets is an ordinary property of the same name in the same file; only the
		// positional record's property was targeted, so its declaration must survive verbatim.
		Assert.Contains("private int Targets { get; set; }", record);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsCheckedOnly_ThenNothingIsWritten()
	{
		string solutionPath = CreateScratchWithPositionalRecord();
		string recordPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "PositionalRecord.cs");

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, "OrderPage.Batch.Targets", "UriByIdLookup", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("resolvedSymbol=Acme.Web.Pages.Orders.OrderPage.Batch.Targets\n", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "PositionalRecord.cs");

		string record = await File.ReadAllTextAsync(recordPath);
		Assert.Contains("Dictionary<string, int> Targets", record);
		Assert.DoesNotContain("UriByIdLookup", record);
	}

	[Fact]
	public async Task WhenAPositionalRecordPropertyIsNotFound_ThenNoCandidateIsANameTheToolThenRejects()
	{
		// The failure this issue reports: the suggestion list offers a fully-qualified name that the tool
		// itself cannot resolve. Whatever the miss path reports, a candidate must round-trip.
		string solutionPath = CreateScratchWithPositionalRecord();

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string miss = await subject.RenameSymbol(solutionPath, "Acme.Web.Pages.Orders.OrderPage.Batch.NoSuchMember", "Renamed", checkOnly: true);
		Assert.Contains("error=NotFound", miss);

		foreach (string candidate in Candidates(miss))
		{
			string retry = await subject.RenameSymbol(solutionPath, candidate, "Renamed", checkOnly: true);
			Assert.DoesNotContain("error=NotFound", retry);
		}
	}

	/// <summary>The 'candidate=' values of a failure block, skipping empty ones the framing may emit.</summary>
	private static IEnumerable<string> Candidates(string result) =>
		result.Split('\n')
			.Where(line => line.StartsWith("candidate=", StringComparison.Ordinal))
			.Select(line => line["candidate=".Length..])
			.Where(candidate => candidate.Length > 0);

	private static string CreateScratchWithPositionalRecord()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		File.WriteAllText(Path.Combine(library, "PositionalRecord.cs"), """
			namespace Acme.Web.Pages.Orders;

			public partial class OrderPage
			{
				private sealed record Batch(List<string> Items, Dictionary<string, int> Targets);

				private sealed class Holder
				{
					private int Targets { get; set; }

					private int Read() => Targets;
				}

				private Batch Build()
				{
					var items = new List<string>();
					var targets = new Dictionary<string, int>();
					return new Batch(items, targets);
				}

				private void Use()
				{
					var (items, targets) = Build();
				}
			}
			""");
		return solutionPath;
	}
}
