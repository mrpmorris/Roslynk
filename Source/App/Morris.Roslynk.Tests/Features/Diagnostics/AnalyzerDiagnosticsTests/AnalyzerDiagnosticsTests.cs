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

		Assert.DoesNotContain("error=", result);
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

		Assert.DoesNotContain("error=", result);
		Assert.All(DiagnosticIds(result), id => Assert.StartsWith("CS", id));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Simple);

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static IReadOnlyList<string> DiagnosticIds(string text)
	{
		// An entry line is '<id>,<line:col>,<message>' (or '<id>,<message>' when there is no location) and sits
		// exactly one tab deeper than its severity label (errors|warnings|infos|hidden). Structural lines can
		// legitimately contain ',' or ' ' — e.g. the generated file obj/.../.NETCoreApp,Version=v8.0.AssemblyAttributes.cs —
		// so depth relative to the severity label, not line content, decides what is an entry.
		var ids = new List<string>();
		int entryDepth = -1;
		foreach (string raw in text.Split('\n'))
		{
			string content = raw.TrimStart('\t');
			int depth = raw.Length - content.Length;

			if (depth > 0 && content is "errors" or "warnings" or "infos" or "hidden")
			{
				entryDepth = depth + 1;
				continue;
			}

			if (depth == entryDepth)
			{
				ids.Add(content[..content.IndexOf(',')]);
				continue;
			}

			if (depth < entryDepth)
				entryDepth = -1;
		}

		return ids;
	}
}
