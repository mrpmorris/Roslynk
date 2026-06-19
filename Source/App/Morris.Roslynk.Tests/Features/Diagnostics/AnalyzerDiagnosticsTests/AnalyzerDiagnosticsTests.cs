using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Diagnostics.AnalyzerDiagnosticsTests;

public class AnalyzerDiagnosticsTests
{
	[Fact]
	public async Task WhenAnalyzersAreIncluded_ThenNonCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Simple, includeWarnings: true, includeInfo: true, includeHidden: true, includeAnalyzers: true);

		Assert.DoesNotContain("#error=", result);
		Assert.Contains(DiagnosticIds(result), id => !id.StartsWith("CS", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenAnalyzersAreExcluded_ThenOnlyCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Simple, includeWarnings: true, includeInfo: true, includeHidden: true, includeAnalyzers: false);

		Assert.DoesNotContain("#error=", result);
		Assert.All(DiagnosticIds(result), id => Assert.StartsWith("CS", id));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Simple);

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static IReadOnlyList<string> DiagnosticIds(string text)
	{
		var ids = new List<string>();
		foreach (string raw in text.Split('\n'))
		{
			if (!raw.StartsWith('\t'))
				continue;

			// An entry is '<id>,<line:col> <message>' or '<id> <message>'; structural lines (project, file,
			// severity label) have no comma or space, so skip them and take the leading id from the rest.
			string content = raw.TrimStart('\t');
			int cut = content.IndexOfAny([',', ' ']);
			if (cut < 0)
				continue;

			ids.Add(content[..cut]);
		}

		return ids;
	}
}
