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
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResponse response = await subject.GetDiagnostics(TestSolutions.MultiTarget, severities: ["error"], targetFramework: "net8.0");

		Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Id == "CS0029");
	}

	[Fact]
	public async Task WhenLimitedToNet10_ThenTheNet8OnlyErrorIsAbsent()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResponse response = await subject.GetDiagnostics(TestSolutions.MultiTarget, severities: ["error"], targetFramework: "net10.0");

		Assert.DoesNotContain(response.Diagnostics, diagnostic => diagnostic.Id == "CS0029");
	}
}
