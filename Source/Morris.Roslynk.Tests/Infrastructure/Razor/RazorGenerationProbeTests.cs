using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

public class RazorGenerationProbeTests
{
	/// <summary>
	/// Documents a known limitation (a canary). The Razor source generator
	/// (<c>Microsoft.CodeAnalysis.Razor.Compiler</c>) is loaded as an analyzer and the <c>.razor</c> file
	/// is present as an additional document, yet no generated documents are produced under
	/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/>: the design-time build does not plumb
	/// the per-<c>AdditionalFiles</c> <c>TargetPath</c> metadata the generator needs. So <c>@code</c>
	/// members and their markup bindings never enter the compilation, which is why Razor read-context
	/// (semantic find-references into <c>.razor</c>) is not implemented. If a future Roslyn/Razor version
	/// starts emitting these documents, this test flips; revisit Razor read-context then.
	/// </summary>
	[Fact]
	public async Task RazorSourceGenerationProducesNoDocumentsUnderMSBuildWorkspace()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Razor);
		Project project = instance.CurrentSolution.Projects.First();

		IEnumerable<SourceGeneratedDocument> generated = await project.GetSourceGeneratedDocumentsAsync();

		Assert.Empty(generated);
	}
}
