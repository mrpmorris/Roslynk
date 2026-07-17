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
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeErrors: true);

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("errors=0", result);
		Assert.Contains(result.Split('\n'), line => line == "BrokenLibrary");
		Assert.Contains("\terrors\n", result);
		Assert.Contains("CS0029,9:27,", result);
	}

	[Fact]
	public async Task WhenSeveritiesAreNotWidened_ThenOnlyErrorsAreReturnedButTheCountsStillSeeWarnings()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeErrors: true);

		// Counts are always in the header; body only shows errors when includeErrors is set.
		Assert.Contains("warnings=0", result);
		Assert.DoesNotContain("\twarnings", result);
	}

	[Fact]
	public async Task WhenWarningsAreIncluded_ThenTheyAppearAlongsideErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeWarnings: true);

		// The fixture has zero warnings, so only errors appear in the body.
		Assert.Contains("warnings=0", result);
		Assert.Contains("\terrors\n", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Broken);
	}
}
