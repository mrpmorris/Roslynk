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
	// Use Path.Combine so planted paths resolve on Linux and Windows (literal backslashes are not separators on Unix).
	private static readonly string GeneratedSubPath = Path.Combine(
		"obj",
		"Debug",
		"net8.0",
		"generated",
		"Microsoft.CodeAnalysis.Razor.Compiler",
		"Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator");

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
		// Cover every component so incompleteness is not the reason the snapshot is rejected — only age is.
		string planted = PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter { void M() { StaleSnapshotMarker(); } } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		File.SetLastWriteTimeUtc(planted, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		// Stale content must not drive compilation (regenerated or dropped); marker would surface as CS0103.
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

		// Markers prove the pre-generated snapshot was dropped when _Imports invalidates it.
		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter { void M() { DirectiveStaleMarker(); } } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter { void M() { DirectiveStaleMarker(); } } }");

		// _Imports.razor changes every component's generated code, so one written after the snapshot
		// invalidates it wholesale (its own _Imports_razor.g.cs is not required to exist).
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");
		File.WriteAllText(Path.Combine(projectDir, "_Imports.razor"), "@using Microsoft.AspNetCore.Components.Web");
		File.SetLastWriteTimeUtc(Path.Combine(projectDir, "_Imports.razor"), DateTime.UtcNow.AddMinutes(5));

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("DirectiveStaleMarker"));
	}

	[Fact]
	public async Task WhenTheSnapshotIncludesTheImportsFilesOwnOutput_ThenItIsStillUsed()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// Every SDK emits _Imports_razor.g.cs for _Imports.razor; it must count as claimed, not as an orphan.
		string imports = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "_Imports.razor");
		File.WriteAllText(imports, "@using Microsoft.AspNetCore.Components.Web");
		File.SetLastWriteTimeUtc(imports, DateTime.UtcNow.AddMinutes(-5));

		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter : global::Microsoft.AspNetCore.Components.ComponentBase { } }");
		PlantGeneratedFile(solutionPath, "_Imports_razor.g.cs",
			"namespace RazorLib { public partial class _Imports : global::Microsoft.AspNetCore.Components.ComponentBase { } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		IEnumerable<Document> documents = workspace.Solution.Projects.SelectMany(p => p.Documents);
		Assert.Contains(documents, d => IsSnapshotFile(d, "Counter_razor.g.cs"));
		Assert.Contains(documents, d => IsSnapshotFile(d, "_Imports_razor.g.cs"));
	}

	[Fact]
	public async Task WhenAFlatLayoutSnapshotIncludesViewImportsAndViewStartOutput_ThenItIsUsed()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "CshtmlLib");
		string viewStart = Path.Combine(projectDir, "Views", "_ViewStart.cshtml");
		File.WriteAllText(viewStart, "@{ Layout = null; }");
		File.SetLastWriteTimeUtc(viewStart, DateTime.UtcNow.AddMinutes(-5));

		// Older SDKs flatten the whole relative path into the file name.
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views_Home_Index_cshtml.g.cs", "namespace AspNetCoreGeneratedDocument { internal sealed class Views_Home_Index { } }");
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views__ViewImports_cshtml.g.cs", "namespace AspNetCoreGeneratedDocument { internal sealed class Views__ViewImports { } }");
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views__ViewStart_cshtml.g.cs", "namespace AspNetCoreGeneratedDocument { internal sealed class Views__ViewStart { } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		IEnumerable<Document> documents = workspace.Solution.Projects.SelectMany(p => p.Documents);
		Assert.Contains(documents, d => IsSnapshotFile(d, "Views_Home_Index_cshtml.g.cs"));
		Assert.Contains(documents, d => IsSnapshotFile(d, "Views__ViewImports_cshtml.g.cs"));
		Assert.Contains(documents, d => IsSnapshotFile(d, "Views__ViewStart_cshtml.g.cs"));
	}

	[Fact]
	public async Task WhenACodeBehindFileIsNewerThanTheSnapshot_ThenTheSnapshotIsNotTrusted()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();

		// Every .razor source is covered and older than its .g.cs, but the generated code also binds to C#: a
		// component parameter renamed in a code-behind after the build would survive in the snapshot.
		PlantGeneratedFile(solutionPath, "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter { void M() { CodeBehindStaleMarker(); } } }");
		PlantGeneratedFile(solutionPath, "UsesCounter_razor.g.cs",
			"namespace RazorLib { public partial class UsesCounter { void M() { CodeBehindStaleMarker(); } } }");
		string codeBehind = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Helper.cs");
		File.WriteAllText(codeBehind, "namespace RazorLib; public static class Helper { }");
		File.SetLastWriteTimeUtc(codeBehind, DateTime.UtcNow.AddMinutes(5));

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("CodeBehindStaleMarker"));
		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(p => p.Documents), d => IsSnapshotFile(d, "Counter_razor.g.cs"));
	}

	[Fact]
	public async Task WhenOnlyAnotherConfigurationHasASnapshot_ThenItIsNotUsed()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string releaseDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "obj", "Release", "net8.0", "generated",
			"Microsoft.CodeAnalysis.Razor.Compiler", "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator");
		Directory.CreateDirectory(releaseDir);
		File.WriteAllText(Path.Combine(releaseDir, "Counter_razor.g.cs"),
			"namespace RazorLib { public partial class Counter { void M() { OtherConfigurationMarker(); } } }");
		File.WriteAllText(Path.Combine(releaseDir, "UsesCounter_razor.g.cs"),
			"namespace RazorLib { public partial class UsesCounter { void M() { OtherConfigurationMarker(); } } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(diagnostics, d => d.GetMessage().Contains("OtherConfigurationMarker"));
		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(p => p.Documents),
			d => d.FilePath?.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) == true);
	}

	private static bool IsSnapshotFile(Document document, string fileName) =>
		document.FilePath is string path
		&& string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)
		&& path.Contains("Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase);

	private static string PlantGeneratedFile(string solutionPath, string fileName, string content) =>
		PlantGeneratedFile(solutionPath, "RazorLib", fileName, content);

	private static string PlantGeneratedFile(string solutionPath, string projectFolder, string fileName, string content)
	{
		string projectDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, projectFolder);
		string generatedDir = Path.Combine(projectDir, GeneratedSubPath);
		Directory.CreateDirectory(generatedDir);

		string path = Path.Combine(generatedDir, fileName);
		File.WriteAllText(path, content);
		return path;
	}
}
