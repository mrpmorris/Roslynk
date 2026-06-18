using Morris.Roslynk.Features.References.FindReferences;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.References.FindReferencesTests;

public class ConditionalFindReferencesTests
{
	[Fact]
	public async Task WhenASymbolIsUsedInBothIfAndElseBranches_ThenReferencesFromBothBranchesAreFound()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Conditional);
		var subject = new FindReferencesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindReferences(TestSolutions.Conditional, "ConditionalLib.Target.Ping");

		Assert.Contains("#resolvedSymbol=ConditionalLib.Target.Ping", result);

		// The solution loads as DEBUG, so the #if DEBUG call (line 9) is active and the #else call (line 11)
		// sits in disabled text. Multi-projection adds a !DEBUG projection, so both calls are reported.
		Assert.Equal(2, LocationCountUnder(result, "method,Run"));
	}

	private static int LocationCountUnder(string text, string declaration)
	{
		foreach (string line in text.Split('\n'))
		{
			string trimmed = line.TrimStart('\t');
			if (!trimmed.StartsWith(declaration + ",", StringComparison.Ordinal))
				continue;

			string[] parts = trimmed.Split(',');
			return parts[2].Split('|').Length;
		}

		return -1;
	}
}
