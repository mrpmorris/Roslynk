using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Diagnostics.GetDiagnosticsTests;

public class GetDiagnosticsTests
{
	[Fact]
	public async Task WhenASolutionHasACompileError_ThenItIsReturnedAsAnError()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		GetDiagnosticsResponse response = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.True(response.Counts.Errors >= 1);
		Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Id == "CS0029" && diagnostic.Severity == "Error");
	}
}
