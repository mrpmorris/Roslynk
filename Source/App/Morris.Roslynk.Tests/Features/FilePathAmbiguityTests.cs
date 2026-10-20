using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features;

/// <summary>
/// Issue #136 acceptance through the published tools: a filePath naming several loaded files is an
/// <c>Ambiguous</c> error carrying every matching solution-relative path, a unique suffix still resolves
/// and echoes <c>resolvedPath</c>, and a genuine miss keeps the tool's <c>NotFound</c> wording.
/// </summary>
public class FilePathAmbiguityTests : IAsyncLifetime
{
	private string SolutionPath = null!;
	private InstanceRegistry Registry = null!;

	public async Task InitializeAsync()
	{
		SolutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Other"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Other", "Widget.cs"),
			"namespace SimpleLibrary;\n\npublic class OtherWidget\n{\n}\n");

		Registry = new InstanceRegistry();
		await Registry.GetOrAddAsync(SolutionPath);
	}

	public Task DisposeAsync()
	{
		Registry.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task WhenABareFileNameMatchesSeveralFiles_ThenGetDiagnosticsReturnsAmbiguousWithBothPaths()
	{
		var subject = new GetDiagnosticsTool(Registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			SolutionPath, includeErrors: true, includeAnalyzers: false, filePath: "Widget.cs");

		Assert.Contains("error=Ambiguous", result);
		Assert.DoesNotContain("error=NotFound", result);
		Assert.Contains("'Widget.cs' matches 2 loaded files.", result);
		Assert.Contains("candidate=SimpleLibrary/Other/Widget.cs\n", result);
		Assert.Contains("candidate=SimpleLibrary/Widget.cs\n", result);
	}

	[Fact]
	public async Task WhenABareFileNameMatchesSeveralFiles_ThenFindDefinitionReturnsAmbiguousWithBothPaths()
	{
		var subject = new FindDefinitionTool(Registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindDefinition(SolutionPath, "Widget.cs", 1, 1);

		Assert.Contains("error=Ambiguous", result);
		Assert.DoesNotContain("error=NotFound", result);
		Assert.Contains("'Widget.cs' matches 2 loaded files.", result);
		Assert.Contains("candidate=SimpleLibrary/Other/Widget.cs\n", result);
		Assert.Contains("candidate=SimpleLibrary/Widget.cs\n", result);
	}

	[Fact]
	public async Task WhenAPathSuffixMatchesExactlyOneFile_ThenGetDiagnosticsEchoesResolvedPath()
	{
		var subject = new GetDiagnosticsTool(Registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			SolutionPath, includeErrors: true, includeAnalyzers: false, filePath: "Other/Widget.cs");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("filter=filePath:Other/Widget.cs\n", result);
		Assert.Contains("resolvedPath=SimpleLibrary/Other/Widget.cs\n", result);
	}

	[Fact]
	public async Task WhenAPathSuffixMatchesExactlyOneFile_ThenFindDefinitionEchoesResolvedPath()
	{
		string callerPath = Path.Combine(Path.GetDirectoryName(SolutionPath)!, "SimpleLibrary", "Caller.cs");
		string text = await File.ReadAllTextAsync(callerPath);
		(int line, int column) = ToLineColumn(text, text.IndexOf("Greeter", StringComparison.Ordinal));
		var subject = new FindDefinitionTool(Registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindDefinition(SolutionPath, "Caller.cs", line, column);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("fullName=SimpleLibrary.Greeter", result);
		Assert.Contains("resolvedPath=SimpleLibrary/Caller.cs\n", result);
	}

	[Fact]
	public async Task WhenFilePathMatchesNoFile_ThenGetDiagnosticsKeepsTheNotFoundWording()
	{
		var subject = new GetDiagnosticsTool(Registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			SolutionPath, includeErrors: true, includeAnalyzers: false, filePath: "Nope/Missing.cs");

		Assert.Contains("error=NotFound", result);
		Assert.Contains(
			"errorMessage=No file 'Nope/Missing.cs' is compiled or loaded in the solution; "
			+ "pass a path relative to the solution folder or an absolute path.",
			result);
	}

	[Fact]
	public async Task WhenFilePathMatchesNoFile_ThenFindDefinitionKeepsTheNotFoundWording()
	{
		var subject = new FindDefinitionTool(Registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindDefinition(SolutionPath, "Nope/Missing.cs", 1, 1);

		Assert.Contains("error=NotFound", result);
		Assert.Contains(
			"errorMessage='Nope/Missing.cs' is not a C# document compiled in the solution.",
			result);
	}

	private static (int Line, int Column) ToLineColumn(string text, int index)
	{
		int line = 1;
		int column = 1;
		for (int i = 0; i < index; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				column = 1;
			}
			else
			{
				column++;
			}
		}

		return (line, column);
	}
}
