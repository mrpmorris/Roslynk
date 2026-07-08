using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Features.Diagnostics.GeneratorDiagnosticsTests;

public class GeneratorDiagnosticsTests
{
	[Fact]
	public async Task WhenAProjectReferencesASourceGenerator_ThenGeneratedTypesAreInTheCompilation()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Generator);

		// The consumer compiles only if HelloGenerator's output is part of the compilation.
		Assert.Contains("errors=0", result);
		Assert.DoesNotContain("CS0246", result);
	}

	[Fact]
	public async Task WhenTheGeneratorAssemblyCannotBeLoaded_ThenTheLoadReportsTheFailure()
	{
		// A scratch copy excludes bin/obj; plant a corrupt DLL at the generator's output path so the
		// analyzer reference resolves to a file that cannot possibly load.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		string corruptDll = Path.Combine(
			Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "bin", "Debug", "netstandard2.0", "GeneratorLib.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(corruptDll)!);
		File.WriteAllText(corruptDll, "not a PE image");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		// The generator cannot run, so the unloadable reference must be called out rather than the
		// compilation silently degrading to phantom CS0246s.
		Assert.Contains(workspace.LoadDiagnostics, d =>
			d.Contains("Analyzer load failed", StringComparison.Ordinal) &&
			d.Contains("GeneratorLib.dll", StringComparison.OrdinalIgnoreCase));
	}
}
