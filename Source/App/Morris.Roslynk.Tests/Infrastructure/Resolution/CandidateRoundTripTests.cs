using Morris.Roslynk.Features.Callers.GetCallers;
using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Features.Symbols.GetSymbol;
using Morris.Roslynk.Features.Symbols.GetSymbolBody;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

/// <summary>
/// The contract issue #34 is about: the candidates a tool prints on an ambiguous match are exactly the
/// strings that tool accepts, so a caller resolves the ambiguity by copying one line back. Each case drives
/// a tool with a name that matches several symbols, then re-runs it with every candidate it emitted.
/// </summary>
public class CandidateRoundTripTests
{
	private const string AmbiguousMethod = "SimpleLibrary.Overloads.Pick";

	[Fact]
	public async Task WhenGetSymbolBodyReportsAmbiguity_ThenEveryCandidateResolvesOnRetry()
	{
		await AssertRoundTripAsync(AmbiguousMethod, async (registry, name) =>
			await new GetSymbolBodyTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbolBody(TestSolutions.Simple, name));
	}

	[Fact]
	public async Task WhenGetSymbolReportsAmbiguity_ThenEveryCandidateResolvesOnRetry()
	{
		await AssertRoundTripAsync(AmbiguousMethod, async (registry, name) =>
			await new GetSymbolTool(registry, new SymbolResolver(), new ProjectionService())
				.GetSymbol(TestSolutions.Simple, name));
	}

	[Fact]
	public async Task WhenGetCallersReportsAmbiguity_ThenEveryCandidateResolvesOnRetry()
	{
		await AssertRoundTripAsync(AmbiguousMethod, async (registry, name) =>
			await new GetCallersTool(registry, new SymbolResolver(), new ProjectionService())
				.GetCallers(TestSolutions.Simple, name));
	}

	[Fact]
	public async Task WhenFindReferencesReportsAmbiguity_ThenEveryCandidateResolvesOnRetry()
	{
		await AssertRoundTripAsync(AmbiguousMethod, async (registry, name) =>
			await new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService())
				.FindReferences(TestSolutions.Simple, name));
	}

	/// <summary>
	/// Drives <paramref name="run"/> with an ambiguous name, asserts the failure carries distinguishable
	/// candidates, then re-runs it with each of them and asserts each one resolved.
	/// </summary>
	private static async Task AssertRoundTripAsync(string ambiguousName, Func<InstanceRegistry, string, Task<string>> run)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);

		string ambiguous = await run(registry, ambiguousName);
		Assert.Contains("error=Ambiguous", ambiguous);

		IReadOnlyList<string> candidates = Candidates(ambiguous);
		Assert.True(candidates.Count > 1, $"Expected several candidates, got: {ambiguous}");
		Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());

		foreach (string candidate in candidates)
		{
			string resolved = await run(registry, candidate);

			Assert.False(
				resolved.Contains("error=Ambiguous", StringComparison.Ordinal)
				|| resolved.Contains("error=NotFound", StringComparison.Ordinal),
				$"Candidate '{candidate}' did not resolve: {resolved}");
		}
	}

	private static IReadOnlyList<string> Candidates(string result) =>
		result
			.Split('\n')
			.Where(line => line.StartsWith("candidate=", StringComparison.Ordinal))
			.Select(line => line["candidate=".Length..].TrimEnd('\r'))
			.ToArray();
}
