using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Diagnostics.AnalyzerDiagnosticsTests;

public class AnalyzerDiagnosticsTests
{
	private static readonly string[] AllSeverities = ["error", "warning", "info", "hidden"];

	[Fact]
	public async Task WhenAnalyzersAreIncluded_ThenNonCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResponse response = await subject.GetDiagnostics(TestSolutions.Simple, AllSeverities, targetFramework: null, includeAnalyzers: true);

		Assert.Contains(response.Diagnostics, diagnostic => !diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenAnalyzersAreExcluded_ThenOnlyCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResponse response = await subject.GetDiagnostics(TestSolutions.Simple, AllSeverities);

		Assert.All(response.Diagnostics, diagnostic => Assert.StartsWith("CS", diagnostic.Id));
	}
}
