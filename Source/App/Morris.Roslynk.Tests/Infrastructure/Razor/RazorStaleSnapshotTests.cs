using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

/// <summary>
/// The pre-generated razor output under <c>obj/…/generated</c> is a build artifact MSBuild never prunes,
/// so it can disagree with the current sources. These tests plant snapshots in a scratch copy of the
/// Razor fixture and assert that only verified-fresh files are trusted.
/// </summary>
public class RazorStaleSnapshotTests
{
	private const string GeneratedSubPath = @"obj\Debug\net8.0\generated\Microsoft.CodeAnalysis.Razor.Compiler\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";

	[Fact]
	public async Task WhenAGeneratedFileHasNoSource_ThenItIsNotAddedToTheSolution()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// An orphan: its Ghost.razor source does not exist (deleted after the last build).
		PlantGeneratedFile(solutionPath, "Ghost_razor.g.cs",
			"namespace RazorLib { public partial class Ghost { void M() { OrphanSnapshotMarker(); } } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.EndsWith("Ghost_razor.g.cs", StringComparison.OrdinalIgnoreCase) == true);
		Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("OrphanSnapshotMarker"));
	}

	[Fact]
	public async Task WhenAGeneratedFileIsOlderThanItsSource_ThenTheSnapshotIsNotTrusted()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// Stale: Counter.razor exists but was edited after this file was emitted.
		string planted = PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter { void M() { StaleSnapshotMarker(); } } }");
		File.SetLastWriteTimeUtc(planted, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.Contains("Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase) == true);
		Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("StaleSnapshotMarker"));
	}

	[Fact]
	public async Task WhenTheSnapshotIsFresh_ThenItsFilesAreUsed()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// Fresh: every .razor source is covered by a .g.cs that is newer than it.
		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		Assert.Contains(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.EndsWith("Counter_razor.g.cs", StringComparison.OrdinalIgnoreCase) == true);
		Assert.Contains(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.EndsWith("UsesCounter_razor.g.cs", StringComparison.OrdinalIgnoreCase) == true);
	}

	[Fact]
	public async Task WhenASourceNameContainsUnderscores_ThenItsGeneratedFileIsNotMistakenForAnOrphan()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// A source whose own name contains an underscore: hint names flatten every special character to
		// '_', so naive un-flattening would misread this as folder My/Widget.razor and orphan it.
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");
		File.WriteAllText(Path.Combine(projectDir, "My_Widget.razor"), "<p>widget</p>");

		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "My_Widget_razor.g.cs",
			"namespace RazorLib { public partial class My_Widget : global::Microsoft.AspNetCore.Components.ComponentBase { } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		// All three sources are covered by newer .g.cs files, so the snapshot is fresh and used.
		Assert.Contains(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.EndsWith("My_Widget_razor.g.cs", StringComparison.OrdinalIgnoreCase) == true);
	}

	[Fact]
	public async Task WhenADirectiveFileIsNewerThanTheSnapshot_ThenTheSnapshotIsNotTrusted()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");

		// _Imports.razor produces no .g.cs of its own but changes every component's generated code, so
		// one written after the snapshot invalidates it wholesale.
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");
		File.WriteAllText(Path.Combine(projectDir, "_Imports.razor"), "@using Microsoft.AspNetCore.Components.Web");
		File.SetLastWriteTimeUtc(Path.Combine(projectDir, "_Imports.razor"), DateTime.UtcNow.AddMinutes(5));

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.Contains("Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase) == true);
	}

	private static string PlantGeneratedFile(string solutionPath, string fileName, string content)
	{
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");
		string generatedDir = Path.Combine(projectDir, GeneratedSubPath);
		Directory.CreateDirectory(generatedDir);

		string path = Path.Combine(generatedDir, fileName);
		File.WriteAllText(path, content);
		return path;
	}
}
