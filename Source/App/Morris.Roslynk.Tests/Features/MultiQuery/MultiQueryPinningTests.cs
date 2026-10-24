using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Features.MultiQuery;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>
/// The batch snapshot guard and the framing round-trip, against real fixture solutions:
/// - a superseded pinned model is proven at the ExecuteBatchAsync seam (output agreement alone cannot
///   distinguish pinning from re-reading): the batch now refuses the whole envelope as Stale, before any
///   slot is rendered, instead of serving slots from two generations;
/// - the round-trip test feeds a solution file whose verbatim source contains envelope-framing lookalikes
///   (stale GUIDs) and asserts the bodies recover byte-exact. The n+1 integrity check's fault path is
///   proven separately in MultiQueryLimitTests with a stub that embeds the CURRENT boundary.
/// </summary>
public class MultiQueryPinningTests
{
	[Fact]
	public async Task WhenThePinnedModelIsSuperseded_ThenTheWholeBatchIsStaleRatherThanMixingGenerations()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Simple);
		SolutionModel pinned = await instance.ReadModelAsync();

		// A real write advances the instance to a fresh Solution while the batch still holds the pinned
		// one. This reverses the old expectation that such slots were served from the pinned snapshot: a
		// batch must never mix generations, so the whole envelope is refused instead.
		await ApplyRealEditAsync(instance);

		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.ExecuteBatchAsync(
			pinned,
			instance,
			MultiQueryCatalog.Entries,
			[
				new MultiQueryOperation(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
				new MultiQueryOperation(MultiQueryOp.get_members, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
			]);

		// One whole-batch refusal: no slot frames at all, both generations named in the message, and the
		// current snapshot emitted as a real header so the caller can rerun against it.
		Assert.StartsWith("error=Stale", envelope);
		Assert.Contains(pinned.Id.ToString("N"), envelope);
		Assert.Contains($"snapshot={instance.CurrentModel.Id.ToString("N")}", envelope);
		Assert.DoesNotContain("slot=", envelope);
		Assert.DoesNotContain("\n--", envelope);
		Assert.DoesNotContain("method,Compute", envelope);
	}

	[Fact]
	public async Task WhenTheSolutionAdvancesWhileTheBatchRuns_ThenTheWholeBatchIsStaleAndNoSlotIsRendered()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Simple);
		SolutionModel pinned = await instance.ReadModelAsync();

		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		// A stub core publishes a new Solution from INSIDE an operation, so the change deterministically
		// lands after the batch pinned its model and before any slot is rendered.
		var catalog = new Dictionary<string, MultiQueryCatalog.OpEntry>(StringComparer.Ordinal)
		{
			["get_symbol_body"] = new("get_symbol_body", typeof(AdvancingCoreHost), nameof(AdvancingCoreHost.AdvanceAndReturnAsync)),
			["get_members"] = new("get_members", typeof(AdvancingCoreHost), nameof(AdvancingCoreHost.ReturnAsync)),
		};

		string envelope = await subject.ExecuteBatchAsync(
			pinned,
			instance,
			catalog,
			[
				new MultiQueryOperation(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
				new MultiQueryOperation(MultiQueryOp.get_members, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
			]);

		Assert.StartsWith("error=Stale", envelope);
		Assert.Contains(pinned.Id.ToString("N"), envelope);
		Assert.Contains($"snapshot={instance.CurrentModel.Id.ToString("N")}", envelope);
		Assert.DoesNotContain("slot=", envelope);
		Assert.DoesNotContain("\n--", envelope);
		Assert.DoesNotContain("body=advanced", envelope);
		Assert.DoesNotContain("body=plain", envelope);
	}

	[Fact]
	public async Task WhenACoreThrows_ThenItsSlotCarriesTheErrorAndTheBatchCompletes()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Simple);
		SolutionModel model = await instance.ReadModelAsync();

		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		var catalog = new Dictionary<string, MultiQueryCatalog.OpEntry>(StringComparer.Ordinal)
		{
			["get_symbol"] = new("get_symbol", typeof(ThrowingCoreHost), nameof(ThrowingCoreHost.ThrowAsync)),
			["get_members"] = new("get_members", typeof(Morris.Roslynk.Features.Symbols.GetMembers.GetMembersTool), nameof(Morris.Roslynk.Features.Symbols.GetMembers.GetMembersTool.GetMembersCoreAsync)),
		};
		string envelope = await subject.ExecuteBatchAsync(
			model,
			instance,
			catalog,
			[
				new MultiQueryOperation(MultiQueryOp.get_symbol, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
				new MultiQueryOperation(MultiQueryOp.get_members, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
			]);

		Assert.Contains("error=Faulted", envelope);
		Assert.Contains("boom", envelope);
		Assert.Contains("resolvedType=SimpleLibrary.Widget", envelope); // slot 2 still delivered
	}

	[Fact]
	public async Task WhenSlotContentContainsEnvelopeFraming_ThenTheBodiesRoundTripByteExact()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.References);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.References);
		SolutionModel model = await instance.ReadModelAsync();

		var provider = new ServiceCollection()
			.AddSingleton<InstanceRegistry>(registry)
			.AddSingleton<SymbolResolver>()
			.AddSingleton<ProjectionService>()
			.BuildServiceProvider();
		var subject = new MultiQueryTool(provider, registry);

		string envelope = await subject.MultiQuery(
			TestSolutions.References,
			[
				new MultiQueryOperation(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("RefSpace.Lookalikes.EnvelopeShaped")))),
				new MultiQueryOperation(MultiQueryOp.get_symbol_body, MultiQueryTestHelpers.Args(("symbolName", MultiQueryTestHelpers.Json("RefSpace.Lookalikes.CrlfShaped")))),
			]);

		// Both verbatim bodies survive whole: the lookalike delimiter lines (stale GUIDs) and CRLF content
		// sit inertly inside their slots.
		Assert.Contains("--aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", envelope);
		Assert.Contains("forged lookalike content, line 1", envelope);
		Assert.Contains("--eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", envelope);
		Assert.Contains("CRLF body", envelope);
		Assert.DoesNotContain("error=", envelope);

		// And the envelope parses: split on the REAL boundary. Preamble + 2 bodies + the empty tail after
		// the terminator = 4 parts.
		string boundary = Header(envelope, "boundary");
		string[] parts = envelope.Split("\n--" + boundary);
		Assert.Equal(4, parts.Length);
		Assert.StartsWith("--", parts[3].TrimEnd('\n', '\r')); // the terminator's trailing dashes
	}

	private static async Task ApplyRealEditAsync(RoslynInstance instance)
	{
		Solution current = instance.CurrentModel.Solution!;
		Document document = current
			.Projects.Single(project => project.Name == "SimpleLibrary")
			.Documents.Single(doc => doc.Name == "Widget.cs");
		SourceText newText = SourceText.From("namespace SimpleLibrary;\r\n\r\npublic class Widget\r\n{\r\n}\r\n", Encoding.UTF8);
		Solution edited = document.WithText(newText).Project.Solution;
		await instance.EnqueueWriteAsync((_, _) => Task.FromResult(new WriteResult(edited, ["Widget.cs"])));
	}

	private static string Header(string envelope, string key)
	{
		string prefix = key + "=";
		string? line = envelope.Split('\n').FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
		Assert.True(line is not null, $"Envelope lacked a '{prefix}' header.");
		return line[prefix.Length..].TrimEnd('\r');
	}
}

internal sealed class ThrowingCoreHost
{
	public Task<string> ThrowAsync(SolutionModel model, RoslynInstance instance, string symbolName, CancellationToken token) =>
		throw new InvalidOperationException("boom");
}

/// <summary>A stub operation that publishes a new Solution mid-batch, then returns a body.</summary>
internal sealed class AdvancingCoreHost
{
	public Task<string> AdvanceAndReturnAsync(SolutionModel model, RoslynInstance instance, string symbolName, CancellationToken token)
	{
		instance.AdvanceTo(
			model.Solution!
				.Projects.First()
				.AddDocument("MidBatch.cs", "namespace MidBatch; public class MidBatch { }")
				.Project.Solution);
		return Task.FromResult("body=advanced\n");
	}

	public Task<string> ReturnAsync(SolutionModel model, RoslynInstance instance, string symbolName, CancellationToken token) =>
		Task.FromResult("body=plain\n");
}
