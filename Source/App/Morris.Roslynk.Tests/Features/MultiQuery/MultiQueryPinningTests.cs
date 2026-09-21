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
/// The pinning proof and the framing round-trip, against real fixture solutions:
/// - pinning is proven at the ExecuteBatchAsync seam with a deliberately stale model (output agreement alone
///   cannot distinguish pinning from re-reading);
/// - the round-trip test feeds a solution file whose verbatim source contains envelope-framing lookalikes
///   (stale GUIDs) and asserts the bodies recover byte-exact. The n+1 integrity check's fault path is
///   proven separately in MultiQueryLimitTests with a stub that embeds the CURRENT boundary.
/// </summary>
public class MultiQueryPinningTests
{
	[Fact]
	public async Task WhenThePinnedModelIsStale_ThenSlotsReflectThePinnedSnapshotNotTheCurrentOne()
	{
		var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance instance = await registry.GetOrBeginAsync(TestSolutions.Simple);
		SolutionModel pinned = await instance.ReadModelAsync();

		// A real write advances the instance to a fresh model; the pinned model still shows the old source.
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
				new MultiQueryOperation(MultiQueryOp.get_members, MultiQueryTestHelpers.Args(("typeName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
			]);

		// PRE-write state in both slots: the old source (three methods) rather than the emptied class.
		Assert.Contains("method,Compute", envelope);
		Assert.Contains("method,UseCompute", envelope);
		Assert.DoesNotContain("error=", envelope);
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
				new MultiQueryOperation(MultiQueryOp.get_members, MultiQueryTestHelpers.Args(("typeName", MultiQueryTestHelpers.Json("SimpleLibrary.Widget")))),
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
