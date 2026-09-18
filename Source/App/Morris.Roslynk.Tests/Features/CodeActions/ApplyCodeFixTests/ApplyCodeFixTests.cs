using Morris.Roslynk.Features.CodeActions.ApplyCodeFix;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.CodeActions.ApplyCodeFixTests;

public class ApplyCodeFixTests
{
	[Fact]
	public async Task WhenFixingAnAnalyzerDiagnosticById_ThenTheFileIsUpdated()
	{
		// The point of issue #9: IDE0005 is listed by get_diagnostics, so it must be fixable here too.
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "IDE0005");

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain(UnnecessaryUsingScenario.UnnecessaryUsing, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenFixingAnAnalyzerDiagnosticCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeter);
		string before = await File.ReadAllTextAsync(greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "IDE0005", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Greeter.cs", result);
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenFixingADiagnosticById_ThenTheFileIsUpdated()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219");

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenNoSuchDiagnosticExists_ThenNotFoundIsReturned()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS9999");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(TestSolutions.Simple, "Widget.cs", "CS0219");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
