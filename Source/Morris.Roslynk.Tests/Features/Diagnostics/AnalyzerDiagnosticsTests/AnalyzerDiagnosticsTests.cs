using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Diagnostics.AnalyzerDiagnosticsTests;

public class AnalyzerDiagnosticsTests
{
	[Fact]
	public async Task WhenAnalyzersAreIncluded_ThenNonCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(
			TestSolutions.Simple, includeWarnings: true, includeInfo: true, includeHidden: true, includeAnalyzers: true);

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Diagnostics!, diagnostic => !diagnostic.Id.StartsWith("CS", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenAnalyzersAreExcluded_ThenOnlyCompilerDiagnosticsAppear()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(
			TestSolutions.Simple, includeWarnings: true, includeInfo: true, includeHidden: true, includeAnalyzers: false);

		Assert.True(result.IsSuccess);
		Assert.All(result.Diagnostics!, diagnostic => Assert.StartsWith("CS", diagnostic.Id));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.Simple);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
