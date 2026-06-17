using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

public class RazorGenerationProbeTests
{
	/// <summary>
	/// The SDK's Razor source generator targets a newer Roslyn than we load, so the workspace's analyzer
	/// loader refuses it and produces no documents. <see cref="Morris.Roslynk.Infrastructure.Razor.RazorDocumentGenerator"/>
	/// works around that by running the generator itself and adding the result as a document, so the component
	/// partial enters the compilation. These tests guard that workaround.
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
