using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Diagnostics.GetDiagnosticsTests;

public class GetDiagnosticsTests
{
	[Fact]
	public async Task WhenASolutionHasACompileError_ThenItIsReturnedAsAnError()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.True(result.IsSuccess);
		Assert.True(result.Counts!.Errors >= 1);
		Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Id == "CS0029" && diagnostic.Severity == "Error");
	}

	[Fact]
	public async Task WhenSeveritiesAreNotWidened_ThenOnlyErrorsAreReturnedButTheCountsStillSeeWarnings()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.True(result.IsSuccess);
		Assert.All(result.Diagnostics!, diagnostic => Assert.Equal("Error", diagnostic.Severity));
		Assert.True(result.Counts!.Warnings >= 1);
	}

	[Fact]
	public async Task WhenWarningsAreIncluded_ThenTheyAppearAlongsideErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.Broken, includeWarnings: true);

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Severity == "Warning");
		Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Severity == "Error");
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Broken);
	}
}
