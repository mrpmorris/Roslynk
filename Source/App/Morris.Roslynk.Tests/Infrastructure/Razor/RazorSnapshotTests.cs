using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Razor;

/// <summary>
/// The pre-generated Razor output under <c>obj/…/generated</c>, used only when no Razor generator can be loaded.
/// It is a build artifact MSBuild never prunes, so these tests plant snapshots in scratch copies of the Razor
/// fixtures and check which of their files would be used.
/// </summary>
public class RazorSnapshotTests
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
	public async Task WhenAGeneratedFileHasNoSource_ThenItIsDropped()
	{
		// An orphan: its Ghost.razor source was deleted after the last build.
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		PlantGeneratedFile(solutionPath, "RazorLib", "Counter_razor.g.cs");
		PlantGeneratedFile(solutionPath, "RazorLib", "Ghost_razor.g.cs");

		IReadOnlyList<string> files = await SnapshotFileNamesAsync(solutionPath);

		Assert.Contains("Counter_razor.g.cs", files);
		Assert.DoesNotContain("Ghost_razor.g.cs", files);
	}

	[Fact]
	public async Task WhenASourceNameContainsUnderscores_ThenItsOutputIsNotMistakenForAnOrphan()
	{
		// Hint names flatten every special character to '_', so naive un-flattening would misread this as
		// folder My/Widget.razor and drop it.
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "My_Widget.razor"), "<p>widget</p>");
		PlantGeneratedFile(solutionPath, "RazorLib", "My_Widget_razor.g.cs");

		Assert.Contains("My_Widget_razor.g.cs", await SnapshotFileNamesAsync(solutionPath));
	}

	[Fact]
	public async Task WhenTheImportsFilesOwnOutputIsPresent_ThenItIsClaimed()
	{
		// Every SDK emits _Imports_razor.g.cs for _Imports.razor; it must count as claimed, not as an orphan.
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "_Imports.razor"), "@using Microsoft.AspNetCore.Components.Web");
		PlantGeneratedFile(solutionPath, "RazorLib", "_Imports_razor.g.cs");

		Assert.Contains("_Imports_razor.g.cs", await SnapshotFileNamesAsync(solutionPath));
	}

	[Fact]
	public async Task WhenAFlatLayoutSnapshotIncludesViewImportsAndViewStartOutput_ThenTheyAreClaimed()
	{
		// Older SDKs flatten the whole relative path into the file name.
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(solutionPath)!, "CshtmlLib", "Views", "_ViewStart.cshtml"), "@{ Layout = null; }");
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views_Home_Index_cshtml.g.cs");
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views__ViewImports_cshtml.g.cs");
		PlantGeneratedFile(solutionPath, "CshtmlLib", "Views__ViewStart_cshtml.g.cs");

		IReadOnlyList<string> files = await SnapshotFileNamesAsync(solutionPath);

		Assert.Contains("Views_Home_Index_cshtml.g.cs", files);
		Assert.Contains("Views__ViewImports_cshtml.g.cs", files);
		Assert.Contains("Views__ViewStart_cshtml.g.cs", files);
	}

	[Fact]
	public async Task WhenOnlyAnotherConfigurationHasASnapshot_ThenNoSnapshotIsFound()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string releaseDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "obj", "Release", "net8.0", "generated",
			"Microsoft.CodeAnalysis.Razor.Compiler", "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator");
		Directory.CreateDirectory(releaseDir);
		File.WriteAllText(Path.Combine(releaseDir, "Counter_razor.g.cs"), "// release build");

		Project project = await LoadProjectAsync(solutionPath);

		Assert.Null(RazorSnapshot.DirectoryFor(project));
	}

	[Fact]
	public async Task WhenAGeneratorIsAvailable_ThenTheSnapshotIsNotUsed()
	{
		// The generator is authoritative; a snapshot can only be as fresh as the last build.
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		PlantGeneratedFile(solutionPath, "RazorLib", "Counter_razor.g.cs",
			"namespace RazorLib { public partial class Counter { void M() { SnapshotMarker(); } } }");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);
		IReadOnlyList<Diagnostic> diagnostics = await new DiagnosticsService().GetAllDiagnosticsAsync(workspace.Solution);

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.GetMessage().Contains("SnapshotMarker"));
		Assert.DoesNotContain(workspace.Solution.Projects.SelectMany(project => project.Documents),
			document => document.FilePath?.Contains("Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase) == true);
	}

	private static async Task<IReadOnlyList<string>> SnapshotFileNamesAsync(string solutionPath)
	{
		Project project = await LoadProjectAsync(solutionPath);
		string directory = RazorSnapshot.DirectoryFor(project) ?? throw new InvalidOperationException("No snapshot directory found.");
		return [.. RazorSnapshot.Files(project, directory).Select(file => Path.GetFileName(file.Path))];
	}

	private static async Task<Project> LoadProjectAsync(string solutionPath)
	{
		MsBuildRegistrar.EnsureRegistered();
		using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
		Solution solution = await workspace.OpenSolutionAsync(solutionPath);
		return solution.Projects.Single();
	}

	private static void PlantGeneratedFile(string solutionPath, string projectFolder, string fileName, string content = "// generated")
	{
		string generatedDir = Path.Combine(Path.GetDirectoryName(solutionPath)!, projectFolder, GeneratedSubPath);
		Directory.CreateDirectory(generatedDir);
		File.WriteAllText(Path.Combine(generatedDir, fileName), content);
	}
}
