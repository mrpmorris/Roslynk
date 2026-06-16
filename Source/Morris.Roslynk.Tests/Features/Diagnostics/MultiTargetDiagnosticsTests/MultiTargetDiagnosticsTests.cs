using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Diagnostics.MultiTargetDiagnosticsTests;

public class MultiTargetDiagnosticsTests
{
	[Fact]
	public async Task WhenLimitedToNet8_ThenTheNet8OnlyErrorIsReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net8.0");

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("CS0029", result);
	}

	[Fact]
	public async Task WhenLimitedToNet10_ThenTheNet8OnlyErrorIsAbsent()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net10.0");

		Assert.DoesNotContain("#error=", result);
		Assert.DoesNotContain("CS0029", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.MultiTarget, targetFramework: "net8.0");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.MultiTarget);
	}
}
