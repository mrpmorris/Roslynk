using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Diagnostics.MultiTargetDiagnosticsTests;

public class MultiTargetDiagnosticsTests
{
	[Fact]
	public async Task WhenLimitedToNet8_ThenTheNet8OnlyErrorIsReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net8.0");

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Id == "CS0029");
	}

	[Fact]
	public async Task WhenLimitedToNet10_ThenTheNet8OnlyErrorIsAbsent()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net10.0");

		Assert.True(result.IsSuccess);
		Assert.DoesNotContain(result.Diagnostics!, diagnostic => diagnostic.Id == "CS0029");
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResult result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net8.0");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
	}
}
