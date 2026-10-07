using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

public class RazorGenerationProbeTests
{
	/// <summary>
	/// When the SDK's Razor source generator targets a newer Roslyn than we load, the workspace's analyzer
	/// loader refuses it and produces no documents; when it loads, its output is immutable. Either way
	/// <see cref="Morris.Roslynk.Infrastructure.Razor.RazorDocumentGenerator"/> runs the generator itself and adds
	/// the result as documents, so the component partial enters the compilation. These tests guard that.
	/// </summary>
	[Fact]
	public async Task WhenARazorProjectIsLoaded_ThenTheGeneratedComponentDocumentIsAdded()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Razor);
		Project project = instance.CurrentSolution.Projects.First();

		Assert.Contains(project.Documents, document =>
			document.Name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
			&& document.Name.Contains("Counter", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenARazorProjectIsLoaded_ThenTheComponentPartialBaseIsInTheCompilation()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Razor);
		Project project = instance.CurrentSolution.Projects.First();

		Compilation compilation = (await project.GetCompilationAsync())!;
		INamedTypeSymbol? counter = compilation.GetTypeByMetadataName("RazorLib.Counter");

		Assert.NotNull(counter);
		Assert.Equal("ComponentBase", counter!.BaseType?.Name);
	}
}
